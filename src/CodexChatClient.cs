using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text;

namespace QuietGPT;

public sealed class CodexChatClient : IDisposable
{
    public sealed class DispatchException : IOException
    {
        public bool MayHaveSent { get; }
        public DispatchException(bool mayHaveSent, string message, Exception inner) : base(message, inner) => MayHaveSent=mayHaveSent;
    }
    public record Connection(string NodePath,string ServerPath,string PipePath,string CallerTaskId);
    private readonly string configPath;
    private Process? process;
    private Connection? connection;
    private ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> pending=new();
    private readonly SemaphoreSlim gate=new(1,1);
    private int nextId;
    public CodexChatClient(string configPath){this.configPath=configPath;}
    public async Task<JsonElement> Call(string tool, object args)
    {
        if(tool is not ("list_threads" or "read_thread" or "send_message_to_thread"))throw new InvalidOperationException("Tool is not part of the Quiet bridge.");
        await gate.WaitAsync();
        try{
            try { await Connect(); }
            catch(Exception ex) when(tool=="send_message_to_thread") { throw new DispatchException(false,"Could not connect to Codex before sending: "+ex.Message,ex); }
            JsonElement result;
            try { result=await Rpc("tools/call",new {name=tool,arguments=args,_meta=new Dictionary<string,string>{{"openai/threadId",connection!.CallerTaskId}}},90); }
            catch(Exception ex) when(tool=="send_message_to_thread") { throw new DispatchException(true,"Codex delivery could not be confirmed: "+ex.Message,ex); }
            if(result.TryGetProperty("isError",out var error)&&error.GetBoolean()){
                string reason=ContentText(result);
                if(tool=="send_message_to_thread"&&IsKnownPreSendRejection(reason))throw new DispatchException(false,"Codex has not confirmed the previous turn yet: "+reason,new IOException(reason));
                throw new IOException("Codex rejected the chat action: "+reason);
            }
            return JsonDocument.Parse(ContentText(result)).RootElement.Clone();
        }finally{gate.Release();}
    }
    private static string ContentText(JsonElement r)=>string.Join("\n",r.GetProperty("content").EnumerateArray().Where(c=>c.GetProperty("type").GetString()=="text").Select(c=>c.GetProperty("text").GetString()));
    internal static bool IsKnownPreSendRejection(string reason)=>reason.Contains("An earlier turn submission is not yet confirmed",StringComparison.OrdinalIgnoreCase);
    private async Task Connect()
    {
        if(process is {HasExited:false})return;
        var saved=JsonSerializer.Deserialize<Connection>(File.ReadAllText(configPath))??throw new IOException("Connect Quiet to Codex first.");
        if(!File.Exists(saved.ServerPath)||!Guid.TryParse(saved.CallerTaskId,out _))throw new IOException("The local Codex connection needs updating.");
        string stableNode=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),@".cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe");
        string node=File.Exists(saved.NodePath)?saved.NodePath:stableNode;
        if(!File.Exists(node))throw new IOException("The local Codex Node runtime is unavailable.");
        // Codex rotates its per-session pipe and sometimes its bundled Node runtime.
        // Probe only Codex's own named pipes, and verify this task before using one.
        var pipes=new List<string>();
        void AddPipe(string? pipe){if(pipe is not null&&pipe.StartsWith(@"\\.\pipe\codex-browser-use-",StringComparison.OrdinalIgnoreCase)&&Guid.TryParse(pipe[@"\\.\pipe\codex-browser-use-".Length..],out _)&&!pipes.Contains(pipe,StringComparer.OrdinalIgnoreCase))pipes.Add(pipe);}
        AddPipe(Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH"));
        AddPipe(saved.PipePath);
        try{foreach(var pipe in Directory.GetFiles(@"\\.\pipe\"))AddPipe(pipe);}catch{ /* The saved pipe can still work. */ }
        Exception? last=null;
        foreach(var pipe in pipes.Take(24))
        {
            try{
                connection=saved with {NodePath=node,PipePath=pipe};
                await ConnectPipe(connection);
                var probe=await Rpc("tools/call",new {name="read_thread",arguments=new {threadId=saved.CallerTaskId,turnLimit=1,maxOutputCharsPerItem=100},_meta=new Dictionary<string,string>{{"openai/threadId",saved.CallerTaskId}}},15);
                if(probe.TryGetProperty("isError",out var error)&&error.GetBoolean())throw new IOException("Codex rejected this connection.");
                using var thread=JsonDocument.Parse(ContentText(probe));
                if(thread.RootElement.GetProperty("thread").GetProperty("id").GetString()!=saved.CallerTaskId)throw new IOException("Codex task mismatch.");
                if(connection!=saved){string tmp=configPath+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(connection));File.Move(tmp,configPath,true);}
                return;
            }catch(Exception ex){last=ex;Dispose();}
        }
        throw new IOException("Codex connection is unavailable. Keep Codex open and refresh the Ray & Mira connection.",last);
    }
    private async Task ConnectPipe(Connection selected)
    {
        var start=new ProcessStartInfo(selected.NodePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        start.ArgumentList.Add(selected.ServerPath);start.Environment["CODEX_APP_TOOLS_PIPE_PATH"]=selected.PipePath;
        process=Process.Start(start)??throw new IOException("Could not start the Codex connector.");
        var current=process;
        var requests=new ConcurrentDictionary<int,TaskCompletionSource<JsonElement>>();pending=requests;
        _=Task.Run(async()=>{try{while(await current.StandardError.ReadLineAsync() is not null){}}catch{}});
        _=Task.Run(async()=>{
            try{while(await current.StandardOutput.ReadLineAsync() is {} line){using var d=JsonDocument.Parse(line);if(d.RootElement.TryGetProperty("id",out var id)&&id.TryGetInt32(out int key)&&requests.TryRemove(key,out var waiter))waiter.TrySetResult(d.RootElement.Clone());}}
            catch{}finally{foreach(var p in requests.ToArray())if(requests.TryRemove(p.Key,out var waiter))waiter.TrySetException(new IOException("Codex connector closed."));}
        });
        try{
            await Rpc("initialize",new {protocolVersion="2024-11-05",capabilities=new {},clientInfo=new {name="quiet-chat-bridge",version="2.4.0"}},15);
            await current.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");await current.StandardInput.FlushAsync();
            await Rpc("tools/list",new {},15);
        }catch{Dispose();throw;}
    }
    private async Task<JsonElement> Rpc(string method,object args,int seconds)
    {
        int id=Interlocked.Increment(ref nextId);var source=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);pending[id]=source;
        try{
            await process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {jsonrpc="2.0",id,method,@params=args}));await process.StandardInput.FlushAsync();
            var reply=await source.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
            if(reply.TryGetProperty("error",out var error))throw new IOException(error.GetProperty("message").GetString());
            return reply.GetProperty("result").Clone();
        }finally{pending.TryRemove(id,out _);}
    }
    public void Dispose(){try{if(process is {HasExited:false})process.Kill();}catch{}process?.Dispose();process=null;}
}
