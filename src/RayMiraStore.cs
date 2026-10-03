using System.IO;
using System.Text.Json;

namespace QuietGPT;

public sealed class RayMiraStore
{
    public const string RayId = "00000000-0000-0000-0000-000000000001";
    public const string CodexId = "00000000-0000-0000-0000-000000000002";
    public record Preferences(bool Enabled = false, string Draft = "", string? RequestId = null);
    public record Request(string RequestId, string RayChatId, string CodexTaskId, string Text, string Prompt, DateTimeOffset ApprovedAt);
    public record Result(string RequestId, string RayChatId, string CodexTaskId, string Status, string Text);
    public string Root { get; }
    public Preferences Settings { get; private set; }
    public RayMiraStore(string root)
    {
        Root = root;
        try { Settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(root,"settings.json"))) ?? new(); }
        catch { Settings = new(); }
    }
    public void Save(bool enabled, string draft) { Settings = Settings with { Enabled=enabled, Draft=draft }; Write("settings.json",Settings); }
    private string JobName(string id, string suffix)
    {
        if(!Guid.TryParseExact(id,"D",out _)) throw new InvalidDataException("Invalid request ID");
        return id+suffix;
    }
    public Request? Current()
    {
        if(Settings.RequestId is not {} id) return null;
        var r=JsonSerializer.Deserialize<Request>(File.ReadAllText(Path.Combine(Root,JobName(id,".request.json"))));
        if(r is null || r.RequestId!=id || r.RayChatId!=RayId || r.CodexTaskId!=CodexId) throw new InvalidDataException("The saved request does not match this connection.");
        return r;
    }
    public Result? ReadResult()
    {
        var r=Current();if(r is null) return null;
        string path=Path.Combine(Root,JobName(r.RequestId,".result.json"));
        if(!File.Exists(path)) return null;
        if(new FileInfo(path).Length>200_000) throw new InvalidDataException("The result is too large to display.");
        var result=JsonSerializer.Deserialize<Result>(File.ReadAllText(path));
        if(result is null || result.RequestId!=r.RequestId || result.RayChatId!=RayId || result.CodexTaskId!=CodexId ||
           result.Status is not ("working" or "needs_input" or "completed" or "failed") || result.Text is null)
            throw new InvalidDataException("The result does not match the approved request.");
        return result;
    }
    public Request Prepare(string text)
    {
        if(!Settings.Enabled) throw new InvalidOperationException("Enable the connection first.");
        if(Current()!=null) throw new InvalidOperationException("Finish or close the existing handoff first.");
        if(string.IsNullOrWhiteSpace(text) || text.Length>24_000) throw new InvalidOperationException("Enter a request of up to 24,000 characters.");
        string id=Guid.NewGuid().ToString();
        string resultPath=Path.Combine(Root,id+".result.json");
        var envelope=JsonSerializer.Serialize(new Result(id,RayId,CodexId,"completed","Your concise result for the user and Ray"),new JsonSerializerOptions{WriteIndented=true});
        string prompt=$"""
        Approved Quiet handoff — Ray & Mira
        Target Codex task: {CodexId} (Quiet GPT browser PROJECT)
        Source Ray chat: {RayId} (Ray & Mira — Workbench)
        Request: {id}

        Please handle the following user-reviewed request within its stated scope:

        {text.Trim()}

        Return protocol:
        - If this is not the target Codex task, stop and tell the user rather than executing it in another task.
        - Check the result file before beginning. If this request is already completed, report that result instead of repeating the work.
        - Keep normal tool permissions and user approvals. Do not send messages to Ray or another task automatically.
        - Write a status/result JSON to this exact local file (request filesystem permission if required):
          {resultPath}
        - Use Status "working", "needs_input", "completed", or "failed" and put a concise explanation in Text. Preserve the three IDs below. Use an atomic file replacement when possible.
        {envelope}
        - Also reply normally here. The user will review the result in Quiet and decide whether to share it with Ray.
        """;
        return new(id,RayId,CodexId,text.Trim(),prompt,DateTimeOffset.UtcNow);
    }
    public void Approve(Request request)
    {
        if(!Settings.Enabled || Current()!=null || request.RayChatId!=RayId || request.CodexTaskId!=CodexId) throw new InvalidOperationException("This handoff is no longer available for approval.");
        Write(JobName(request.RequestId,".request.json"),request);
        Settings=Settings with {RequestId=request.RequestId};Write("settings.json",Settings);
    }
    public void CloseHandoff()
    {
        Settings=Settings with {RequestId=null};Write("settings.json",Settings);
    }
    public string ReturnText(Result result) => $"Mira's result for our approved request:\n\n{result.Text}\n\nPlease explain this to me and discuss the next choice. Do not dispatch further work unless I explicitly approve it.";
    private void Write(string name,object value)
    {
        Directory.CreateDirectory(Root);
        string path=Path.Combine(Root,name), temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temp,path,true);
    }
}
