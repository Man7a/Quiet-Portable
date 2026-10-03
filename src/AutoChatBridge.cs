using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuietGPT;

public sealed class AutoChatBridge : IDisposable
{
    public sealed record Chat(string Id,string Title,string Kind,string Status) { public override string ToString()=>Title; }
    public sealed class Binding
    {
        public string Id {get;set;}=Guid.NewGuid().ToString();
        public string RayId {get;set;}=""; public string RayTitle {get;set;}="";
        public string MiraId {get;set;}=""; public string MiraTitle {get;set;}="";
        public bool Enabled {get;set;} public double Since {get;set;}
        public int IntroductionVersion {get;set;}
        public HashSet<string> Seen {get;set;}=[];
        public override string ToString()=>RayTitle+"  ↔  "+MiraTitle;
    }
    public sealed class Job
    {
        public string Id {get;set;}=Guid.NewGuid().ToString(); public string BindingId {get;set;}="";
        public string RayId {get;set;}=""; public string MiraId {get;set;}="";
        public string Text {get;set;}="";public string RayContext {get;set;}="";
        public string SourceTurnId {get;set;}="";
        public string State {get;set;}="queued";public string Result {get;set;}="";public string Info {get;set;}="Waiting for Codex";
        public DateTimeOffset RetryAfter {get;set;}=DateTimeOffset.MinValue;
        public string CollaborationId {get;set;}="";
        public string Scope {get;set;}="";
        public string Discussion {get;set;}="";
        public int Round {get;set;}
        public bool Planning {get;set;}
        public bool Introduction {get;set;}
        public bool SwitchSession {get;set;}
        public bool Stopped {get;set;}
        public bool AwaitingUser {get;set;}
        public string UserUpdates {get;set;}="";
        public string ResultStatus {get;set;}="completed";
        public string ReturnText {get;set;}="";
        public HashSet<string> ReturnBaseline {get;set;}=[];
        public string ReturnTurnId {get;set;}="";
        public string ModeLabel=>Introduction?"Introducing Ray to the bridge":CollaborationId.Length==0?"Single request":Planning?"Ray is preparing the task":$"Working together · Mira reply {Round}";
        public DateTimeOffset Created {get;set;}=DateTimeOffset.UtcNow;
    }
    private sealed class Saved {public List<Binding> Bindings {get;set;}=[];public List<Job> Jobs {get;set;}=[];}
    private Saved data=new();
    private readonly string root;
    private readonly CodexChatClient client;
    private readonly Func<string,object,Task<JsonElement>> call;
    private readonly Func<Job,Task<bool>> prepareReturn;
    private readonly Func<Job,string,Task> completeReturn;
    private bool ticking,disposed;
    public IReadOnlyList<Binding> Bindings=>data.Bindings;
    public IReadOnlyList<Job> Jobs=>data.Jobs;
    public string Status {get;private set;}="Off · Connect a Ray chat to a Codex task";
    public List<Chat> Chats {get;private set;}=[];
    public event Action? Changed;
    public AutoChatBridge(string dataRoot,Func<Job,Task<bool>> prepareReturn,Func<Job,string,Task> completeReturn,Func<string,object,Task<JsonElement>>? toolCall=null)
    {
        root=Path.Combine(dataRoot,"ChatBridge");this.prepareReturn=prepareReturn;this.completeReturn=completeReturn;
        client=new CodexChatClient(Path.Combine(dataRoot,"codex-chat-connection.json"));call=toolCall??client.Call;
        if(File.Exists(Path.Combine(root,"state.json")))data=JsonSerializer.Deserialize<Saved>(File.ReadAllText(Path.Combine(root,"state.json")))??new();
        foreach(var job in data.Jobs)if(job.State is "dispatching" or "returning") {job.State=job.State=="returning"?"return-uncertain":"uncertain";job.Info="Connection stopped during delivery. Check the destination before retrying.";}
    }
    public static bool WantsMira(string text)=>Regex.IsMatch(text,@"^\s*(?:@Mira\b|(?:Ray[,!:]?\s+)?(?:(?:can|could|would)\s+you\s+)?(?:please\s+)?(?:ask|tell|have|get)\s+@Mira\b)",RegexOptions.IgnoreCase);
    public static bool WantsCollaboration(string text)=>Regex.IsMatch(text,@"^\s*Ray[,!:]?\s+(?:(?:can|could|would)\s+you\s+)?(?:please\s+)?work\s+with\s+@?Mira\b",RegexOptions.IgnoreCase);
    public static bool WantsStop(string text)=>Regex.IsMatch(text,@"^\s*Ray[,!:]?\s+(?:please\s+)?stop\s+(?:working\s+with\s+@?Mira|(?:the\s+)?collaboration)\s*[.!]?\s*$",RegexOptions.IgnoreCase);
    public void Stop(Job job)
    {
        foreach(var j in data.Jobs.Where(x=>job.CollaborationId.Length>0?x.CollaborationId==job.CollaborationId:x.Id==job.Id)){
            j.Stopped=true;j.AwaitingUser=false;
            if(j.State=="queued"||(j.Planning&&j.State=="result"))j.State="done";
            j.Info=j.State=="done"?"Stopped · No further work will be sent":"Stopped · Already-sent work may still finish";
        }
        Save();Changed?.Invoke();
    }
    // An ambiguous send can only be repeated after a person checks the target task.
    public void RetryChecked(Job job)
    {
        if(!data.Jobs.Contains(job)||job.State!="uncertain"||job.Stopped)throw new InvalidOperationException("Only an uncertain request can be retried after checking Codex.");
        if(File.Exists(ResultPath(job)))throw new InvalidOperationException("A result already exists for this request.");
        job.State="queued";job.RetryAfter=DateTimeOffset.MinValue;job.Info="Retry requested after checking Codex";Save();Changed?.Invoke();
    }
    // Only an address at the start of a prose line routes a message.
    public static string AddressedMessage(string text)
    {
        string? fence=null; var lines=text.Replace("\r\n","\n").Split('\n');
        for(int i=0;i<lines.Length;i++) {
            string line=lines[i];
            var f=Regex.Match(line,@"^ {0,3}(`{3,}|~{3,})");
            if(f.Success){string marker=f.Groups[1].Value;if(fence==null)fence=marker;else if(marker[0]==fence[0]&&marker.Length>=fence.Length)fence=null;continue;}
            if(fence!=null)continue;
            var m=Regex.Match(line,@"^ {0,3}@Mira(?=$|[\s,:!])[,:!]?\s*",RegexOptions.IgnoreCase);
            if(!m.Success)continue;
            string body=string.Join("\n",new[]{line[m.Length..]}.Concat(lines.Skip(i+1))).Trim();
            return body.Length is >0 and <=12000?body:"";
        }
        return "";
    }
    public static (string Action,string Request) RayDecision(string text,string id)
    {
        string request=AddressedMessage(text);
        return request.Length>0?("request",request):("done","");
    }
    private string RayPrompt(Job j)
    {
        if(!j.Introduction)return "Mira: "+(j.Result.Length<=16000?j.Result:j.Result[..16000]+"\n\nThe full result is available in the linked Codex task.");
        return """
        Quiet bridge update from Mira: this Ray chat is connected to Mira in Codex. These instructions replace the previous bridge markers and reply limits.
        Talk naturally with the user. When you need Mira's help within the user's agreed task, put @Mira at the beginning of a new line, followed by your self-contained request. Everything from that address to the end is sent to Mira, so put anything for the user BEFORE it and nothing for the user afterwards. Put the entire message, including the address, in your normal visible FINAL reply, never an intermediate/commentary response. Always finish that final reply; Quiet does the routing afterwards. Do not put the address in a quote or code block. Ordinary mentions of Mira do not send anything.
        Quiet sends only the words you address to Mira. Include any context or limits Mira needs in that addressed message. If needed facts or permission are missing, ask the user.
        Mira's replies arrive as plain messages beginning 'Mira:'. Explain the result naturally. Address Mira again only when another useful question or action is needed, never merely to acknowledge a reply. Ask the user about missing information or permission; do not expand their scope yourself.
        Do not write MIRA_REQUEST, QUIET_DONE, IDs, or status markers. The connection stays enabled until the user turns it off. For now, briefly acknowledge the connection and wait for the user's next request; do not resume old work.
        """;
    }
    public static string ItemText(JsonElement item)
    {
        if(item.TryGetProperty("text",out var text)&&text.ValueKind==JsonValueKind.String)return text.GetString()??"";
        if(item.TryGetProperty("content",out var content)&&content.ValueKind==JsonValueKind.Array)return string.Join("\n",content.EnumerateArray().Where(x=>x.TryGetProperty("text",out _)).Select(x=>x.GetProperty("text").GetString()));
        return "";
    }
    private static string UserText(JsonElement turn)=>string.Join("\n",turn.GetProperty("items").EnumerateArray().Where(i=>i.GetProperty("type").GetString()=="userMessage").Select(ItemText));
    private static string Answer(JsonElement turn)=>string.Join("\n",turn.GetProperty("items").EnumerateArray().Where(i=>i.GetProperty("type").GetString()=="agentMessage").Select(ItemText));
    public async Task RefreshChats()
    {
        var d=await call("list_threads",new {limit=50});var list=new List<Chat>();
        foreach(var name in new[]{"pinnedThreads","threads"})if(d.TryGetProperty(name,out var entries))foreach(var c in entries.EnumerateArray())
        {
            string kind=c.GetProperty("kind").GetString()??"";
            if(kind is not ("chatgpt" or "codex"))continue;
            if(kind=="codex"&&c.TryGetProperty("hostId",out var host)&&host.GetString()!="local")continue;
            string status=c.TryGetProperty("status",out var st)?st.ValueKind==JsonValueKind.String?st.GetString()??"":st.GetProperty("type").GetString()??"":"";
            list.Add(new(c.GetProperty("id").GetString()!,c.GetProperty("title").GetString()??"Untitled",kind,status));
        }
        Chats=list.DistinctBy(x=>x.Id).ToList();Status="Codex connected · No clipboard needed";Changed?.Invoke();
    }
    public void Add(Chat ray,Chat mira)
    {
        if(ray.Kind!="chatgpt"||mira.Kind!="codex"||!Guid.TryParse(ray.Id,out _)||!Guid.TryParse(mira.Id,out _))throw new InvalidOperationException("Choose a Ray chat and a local Codex task.");
        if(data.Bindings.Any(b=>b.RayId==ray.Id))throw new InvalidOperationException("That Ray chat already has a connection. Disable or remove its existing pair first.");
        data.Bindings.Add(new(){RayId=ray.Id,RayTitle=ray.Title,MiraId=mira.Id,MiraTitle=mira.Title});Save();Changed?.Invoke();
    }
    public void SetEnabled(Binding b,bool enabled)
    {
        if(!data.Bindings.Contains(b))return;
        if(b.Enabled==enabled)return;
        b.Enabled=enabled;
        if(enabled){b.Since=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;Introduce(b);}
        else foreach(var job in data.Jobs.Where(j=>j.BindingId==b.Id&&(j.State!="done"||j.AwaitingUser)).ToArray())Stop(job);
        Save();Changed?.Invoke();
    }
    private void Introduce(Binding b)
    {
        var job=new Job{BindingId=b.Id,RayId=b.RayId,MiraId=b.MiraId,Text="Ray is learning how to coordinate with Mira. Tell her what you want to work on next.",Introduction=true,SwitchSession=true,Planning=true,State="result",Info="Sending Ray the instructions automatically"};
        job.CollaborationId=job.Id;data.Jobs.Add(job);b.IntroductionVersion=2;Save();
    }
    public void Remove(Binding b)
    {
        if(data.Jobs.Any(j=>j.BindingId==b.Id&&(j.State!="done"||j.AwaitingUser)))throw new InvalidOperationException("This connection still has an unfinished request. Turn it off to pause it.");
        data.Bindings.Remove(b);Save();Changed?.Invoke();
    }
    public string ResultPath(Job job)=>Path.Combine(root,job.Id+".result.json");
    public string Prompt(Job j)
    {
        // Ray's addressed message is the entire work request. Scope and earlier
        // chat stay in Quiet's state; Quiet does not turn them into instructions.
        return $$"""
        {{j.RayContext}}
        Save {"Status":"completed","Text":"<reply>"} atomically to {{ResultPath(j)}}; reuse an existing result. If blocked, use "needs_input" or "failed" with a reason. Reply here.
        """;
    }
    public async Task Tick()
    {
        if(ticking||disposed||!data.Bindings.Any(b=>b.Enabled))return;ticking=true;
        try{
            await RefreshChats();
            foreach(var b in data.Bindings.Where(b=>b.Enabled&&b.IntroductionVersion<2).ToArray()){
                // An old setup may belong to a superseded ChatGPT branch. It is not work to replay.
                var oldSetups=data.Jobs.Where(j=>j.BindingId==b.Id&&j.Introduction&&j.State!="done").ToArray();
                if(oldSetups.Length>0){
                    var currentHistory=await call("read_thread",new {threadId=b.RayId,turnLimit=10,maxOutputCharsPerItem=20000});
                    var latest=currentHistory.GetProperty("turns").EnumerateArray().FirstOrDefault(t=>t.GetProperty("status").GetString()=="completed");
                    if(latest.ValueKind==JsonValueKind.Undefined)continue;
                    var reply=latest.GetProperty("items").EnumerateArray().LastOrDefault(i=>i.GetProperty("type").GetString()=="agentMessage");
                    if(reply.ValueKind==JsonValueKind.Undefined)continue;
                    foreach(var old in oldSetups){
                        await completeReturn(old,reply.GetProperty("id").GetString()!);
                        old.State="done";old.Stopped=true;old.AwaitingUser=false;old.Info="Previous setup retired · Refreshing before new instructions";
                    }
                }
                if(!data.Jobs.Any(j=>j.BindingId==b.Id&&j.State!="done")) {b.Since=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000d;Introduce(b);}
            }
            foreach(var b in data.Bindings.Where(b=>b.Enabled&&b.IntroductionVersion>=2).ToArray()){
                if(disposed||!b.Enabled)break;
                var history=await call("read_thread",new {threadId=b.RayId,turnLimit=10,maxOutputCharsPerItem=20000});
                if(!b.Enabled||disposed)continue;
                var turns=history.GetProperty("turns").EnumerateArray().Reverse().ToArray();
                foreach(var turn in turns){
                    if(turn.GetProperty("status").GetString()!="completed")continue;
                    string id=turn.GetProperty("id").GetString()!;
                    // A completed snapshot can still be stale or missing the end of Ray's reply.
                    // Only an actual queued handoff, not mere observation, consumes a source turn.
                    string userText=UserText(turn);
                    if(data.Jobs.Any(j=>j.BindingId==b.Id&&(j.SourceTurnId==id||
                        (b.Seen.Contains(id)&&j.SourceTurnId.Length==0&&!j.Introduction&&
                         (j.Scope==userText||j.Text==userText)))))continue;
                    double started=turn.TryGetProperty("startedAt",out var at)&&at.ValueKind==JsonValueKind.Number?at.GetDouble():0;
                    if(started<b.Since)continue;
                    string text=UserText(turn),answer=Answer(turn);
                    // Returned bridge turns are consumed by Advance, retaining their original scope.
                    if(text.StartsWith("[Quiet result ",StringComparison.Ordinal)||data.Jobs.Any(j=>j.RayId==b.RayId&&j.ReturnText.Length>0&&j.ReturnText==text))continue;
                    if(WantsStop(text)){SetEnabled(b,false);b.Seen.Add(id);Save();break;}
                    string request=AddressedMessage(answer);
                    if(request.Length>0){
                        string discussion=string.Join("\n\n",turns.TakeWhile(t=>t.GetProperty("id").GetString()!=id).Where(t=>!UserText(t).StartsWith("Mira:")&&!UserText(t).StartsWith("Quiet bridge update")&&!UserText(t).StartsWith("Quiet has connected")&&!UserText(t).StartsWith("[Quiet result ")).TakeLast(2).Select(t=>"User: "+UserText(t)+"\nRay: "+Answer(t)));
                        if(discussion.Length>6000)discussion=discussion[^6000..];
                        var job=new Job{BindingId=b.Id,SourceTurnId=id,RayId=b.RayId,MiraId=b.MiraId,Text=request,Scope=text,Discussion=discussion,RayContext=request};
                        job.CollaborationId=job.Id;data.Jobs.Add(job);
                        b.Seen.Add(id);Save();
                    }
                }
            }
            foreach(var job in data.Jobs.Where(j=>j.State!="done").OrderBy(j=>j.Created).ToArray()){
                var binding=data.Bindings.FirstOrDefault(b=>b.Id==job.BindingId);if(binding?.Enabled!=true||disposed)continue;
                await Advance(job,binding);
            }
            var attention=data.Jobs.FirstOrDefault(j=>!j.Stopped&&j.State=="uncertain"&&data.Bindings.Any(b=>b.Id==j.BindingId&&b.Enabled));
            var retry=data.Jobs.FirstOrDefault(j=>!j.Stopped&&j.State=="queued"&&j.RetryAfter>DateTimeOffset.UtcNow);
            Status=attention!=null?"Bridge needs attention · "+attention.Info:retry!=null?"Codex retry pending · "+retry.Info:data.Bindings.Any(b=>b.Enabled)?"Ready · Talk normally with Ray; no special phrase needed":"Off · Enable a connected chat to start another task";
        }catch(Exception ex){Status="Connection needs attention · "+ex.Message;}
        finally{ticking=false;Changed?.Invoke();}
    }
    private async Task Advance(Job j,Binding b)
    {
        if(j.State is "sent" or "uncertain"){
            if(File.Exists(ResultPath(j))){
                if(new FileInfo(ResultPath(j)).Length>200000)throw new IOException("Result exceeds the bridge size limit.");
                using var doc=JsonDocument.Parse(File.ReadAllText(ResultPath(j)));var r=doc.RootElement;
                // The unique result path selects the job. Legacy files may also carry IDs;
                // reject any mismatch instead of letting stale results cross conversations.
                if((r.TryGetProperty("RequestId",out var rid)&&rid.GetString()!=j.Id)||
                   (r.TryGetProperty("RayChatId",out var ray)&&ray.GetString()!=j.RayId)||
                   (r.TryGetProperty("CodexTaskId",out var mira)&&mira.GetString()!=j.MiraId))throw new IOException("Result identifiers do not match the request.");
                string status=r.GetProperty("Status").GetString()??"";
                if(status=="working"){j.Info="Mira is working";return;}
                if(status is "completed" or "failed" or "needs_input") {j.Result=r.GetProperty("Text").GetString()??"";j.ResultStatus=status;j.State="result";j.Info=status=="needs_input"?"Mira needs your input · Returning to Ray":"Returning result to Ray";Save();}
            }
        }
        if(j.State=="queued"){
            if(j.RetryAfter>DateTimeOffset.UtcNow)return;
            if(data.Jobs.Any(other=>other.Id!=j.Id&&other.MiraId==j.MiraId&&other.State is "sent" or "dispatching" or "uncertain")){j.Info="Queued behind another request for this task";return;}
            var target=Chats.FirstOrDefault(c=>c.Id==j.MiraId);
            if(target?.Status!="idle"){j.Info="Waiting for the Codex task to be idle";return;}
            var current=await call("read_thread",new {threadId=j.MiraId,turnLimit=1,maxOutputCharsPerItem=100});
            if(!current.TryGetProperty("thread",out var targetThread)||targetThread.GetProperty("id").GetString()!=j.MiraId||!targetThread.TryGetProperty("status",out var targetStatus)||!targetStatus.TryGetProperty("type",out var targetType)||targetType.GetString()!="idle"){j.Info="Waiting for the Codex task to be idle";return;}
            if(!b.Enabled||disposed)return;
            j.State="dispatching";j.Info="Sending to Mira";Save();
            try{
                var sent=await call("send_message_to_thread",new {threadId=j.MiraId,prompt=Prompt(j)});
                if(sent.TryGetProperty("threadId",out var acknowledged)&&acknowledged.GetString()!=j.MiraId)throw new IOException("Codex acknowledged a different target task.");
                j.State="sent";j.Info="Sent to Mira · Waiting for her update";
            }
            catch(CodexChatClient.DispatchException ex) when(!ex.MayHaveSent){j.State="queued";j.RetryAfter=DateTimeOffset.UtcNow.AddSeconds(30);j.Info="Codex did not accept the send; Quiet will retry. "+ex.Message;throw;}
            catch(Exception ex){j.State="uncertain";j.Info="Delivery could not be confirmed. Check the Codex task before choosing Retry in Activity. "+ex.Message;throw;}
            finally{Save();}
        }
        if(j.State=="result"){
            if(j.RetryAfter>DateTimeOffset.UtcNow)return;
            if(!await prepareReturn(j)){j.Info="Result ready · Waiting for a safe moment in the Ray chat";return;}
            if(!b.Enabled||disposed)return;
            var before=await call("read_thread",new {threadId=j.RayId,turnLimit=10,maxOutputCharsPerItem=20000});
            if(!b.Enabled||disposed)return;
            j.ReturnBaseline=before.GetProperty("turns").EnumerateArray().Select(t=>t.GetProperty("id").GetString()!).ToHashSet();
            j.ReturnText=RayPrompt(j);
            j.State="returning";j.Info="Sending result to Ray";Save();
            try{
                await call("send_message_to_thread",new {threadId=j.RayId,prompt=j.ReturnText});
                j.State="returned";j.Info="Ray is preparing her response";
            }catch(CodexChatClient.DispatchException ex) when(!ex.MayHaveSent){j.State="result";j.RetryAfter=DateTimeOffset.UtcNow.AddSeconds(20);j.Info="Connection failed before returning to Ray; Quiet will retry. "+ex.Message;throw;}
            catch(Exception ex){j.State="return-uncertain";j.Info="Return delivery uncertain. Checking Ray before any further action. "+ex.Message;throw;}
            finally{Save();}
        }
        if(j.State is "returned" or "return-uncertain"){
            var history=await call("read_thread",new {threadId=j.RayId,turnLimit=10,maxOutputCharsPerItem=20000});
            foreach(var turn in history.GetProperty("turns").EnumerateArray()){
                string turnId=turn.GetProperty("id").GetString()!;
                bool matches=j.ReturnText.Length==0?UserText(turn).StartsWith("[Quiet result "+j.Id+"]",StringComparison.Ordinal):UserText(turn)==j.ReturnText&&!j.ReturnBaseline.Contains(turnId)&&!data.Jobs.Any(other=>other.Id!=j.Id&&other.ReturnTurnId==turnId);
                if(turn.GetProperty("status").GetString()!="completed"||!matches)continue;
                var reply=turn.GetProperty("items").EnumerateArray().LastOrDefault(i=>i.GetProperty("type").GetString()=="agentMessage");
                if(reply.ValueKind==JsonValueKind.Undefined)continue;
                await completeReturn(j,reply.GetProperty("id").GetString()!);
                j.ReturnTurnId=turnId;b.Seen.Add(turnId);j.State="done";j.Info="Delivered to Ray";
                if(j.CollaborationId.Length>0)FinishRayDecision(j,ItemText(reply),false);
                Save();break;
            }
        }
    }
    private void FinishRayDecision(Job j,string answer,bool userReplied)
    {
        var decision=RayDecision(answer,j.Id);
        if(!j.Introduction&&!j.Stopped&&j.ResultStatus=="completed"&&decision.Action=="request"){
            data.Jobs.Add(new(){BindingId=j.BindingId,RayId=j.RayId,MiraId=j.MiraId,CollaborationId=j.CollaborationId,Scope=j.Scope,Discussion=j.Discussion,UserUpdates=j.UserUpdates,Round=j.Round+1,Text=decision.Request,RayContext=decision.Request});
            j.Info="Ray's follow-up is queued for Mira";
        }else j.Info=j.Introduction?"Connected · Chat normally with Ray":"Delivered · Connection remains on";
        j.AwaitingUser=false;
    }
    private void Save(){Directory.CreateDirectory(root);string p=Path.Combine(root,"state.json"),tmp=p+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(data,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,p,true);}
    public void Dispose(){disposed=true;client.Dispose();}
}
