using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QuietGPT;

// Local artwork has a separate WebView profile and cannot navigate to chat or external sites.
public sealed partial class RiggedAvatarView : WebView2CompositionControl
{
    private string? profileRoot;
    private bool starting, disposed, running;
    private string activity = "idle";
    public bool Ready { get; private set; }
    public bool Running => Ready && running;
    public string? LastError { get; private set; }
    public event Action? AvatarReady;
    public event Action? ReadReplyRequested;
    public event Action? OpenQuietRequested;
    public event Action? SettingsRequested;
    public event Action? MenuRequested;
    public event Action<bool>? ShoulderDragRequested;
    public void OpenStyle() => Post(new {type="open-style"});
    public event Action? AvatarFailed;
    private TaskCompletionSource<string>? speechDone;
    private string? speechId;
    private bool speechPaused;
    private double speechRate=1;
    public void SetSpeechPaused(bool value) { speechPaused=value;Post(new {type="pause-speech",paused=value}); }
    public void SetSpeechRate(double value) { if(!double.IsFinite(value))return;speechRate=Math.Clamp(value,.65,1.5);Post(new {type="speech-rate",rate=speechRate}); }


    public RiggedAvatarView()
    {
        DefaultBackgroundColor = System.Drawing.Color.Transparent;
        Loaded += async (_, _) => await InitializeAvatar();
    }
    public void Configure(string root) => profileRoot = Path.Combine(root, "AvatarProfile");
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        // The composition capture pool rejects zero-sized surfaces when WPF collapses a parent.
        // Keep its last valid surface; rendering is separately suspended by SetRunning(false).
        if (sizeInfo.NewSize.Width < 1 || sizeInfo.NewSize.Height < 1) return;
        base.OnRenderSizeChanged(sizeInfo);
    }
    private async Task InitializeAvatar()
    {
        if (starting || disposed || profileRoot is null) return;
        starting = true;
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "Avatar");
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("The rigged avatar artwork is missing.");
            // Read is initiated by native WPF, which does not transfer a browser user gesture.
            // This environment hosts only our local avatar, never the ChatGPT page.
            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: profileRoot, options: options);
            if (disposed) return;
            await EnsureCoreWebView2Async(env);
            var core = CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping("avatar.quiet.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, e) => { if (e.Uri != "https://avatar.quiet.local/index.html") e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            await EnableOutputDiscovery(core);
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != "https://avatar.quiet.local/index.html") return;
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    if(doc.RootElement.GetProperty("kind").GetString() is "audio-output-result" or "audio-output-devices-changed"){OutputMessage(doc.RootElement);return;}
                    if(doc.RootElement.GetProperty("kind").GetString()=="companion-settings"){if(Running)SettingsRequested?.Invoke();return;}
                    if(doc.RootElement.GetProperty("kind").GetString()=="companion-menu"){if(Running)MenuRequested?.Invoke();return;}
                    if(doc.RootElement.GetProperty("kind").GetString()=="shoulder-drag"){ShoulderDragRequested?.Invoke(Running&&doc.RootElement.GetProperty("start").GetBoolean());return;}
                    if (doc.RootElement.GetProperty("kind").GetString() == "open-quiet") { if(Running)OpenQuietRequested?.Invoke(); return; }
                    if (doc.RootElement.GetProperty("kind").GetString() == "read-reply") { if (Running) ReadReplyRequested?.Invoke(); return; }
                    if (doc.RootElement.GetProperty("kind").GetString() == "speech-finished")
                    {
                        if (doc.RootElement.GetProperty("id").GetString() == speechId)
                            speechDone?.TrySetResult(doc.RootElement.GetProperty("reason").GetString() ?? "error");
                        return;
                    }
                    if (doc.RootElement.GetProperty("kind").GetString() != "avatar-ready") return;
                    Ready = true; SetActivity(activity); SetRunning(running); AvatarReady?.Invoke();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
            };
            core.ProcessFailed += (_, _) => Fail("The avatar renderer stopped.");
            core.Navigate("https://avatar.quiet.local/index.html");
        }
        catch (Exception ex) { if (!disposed) Fail(ex.Message); }
    }
    private void Fail(string message) { Ready = false; LastError = message; AvatarFailed?.Invoke(); }
    private void Post(object value)
    {
        if (!Ready || disposed) return;
        try { CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
    public void SetRunning(bool value) { running = value; Post(new { type = "running", value }); }
    public void SetActivity(string value)
    {
        activity = value;
        // Writing text is not speech. Mouth animation is reserved for actual voice playback.
        Post(new { type = "activity", state = value == "thinking" ? "thinking" : "idle", mood = value is "done" or "download" ? "happy" : value == "error" ? "thoughtful" : "neutral" });
    }
    public void MoveWindow(double x, double y) => Post(new { type = "motion", x, y });
    public void SetSpatialPosition(double position) => Post(new { type="spatial-position", position });
    public async Task PlaySpeech(JsonElement bundle, CancellationToken token, string? mood = null)
    {
        if (!Ready || !running) throw new InvalidOperationException("Show the companion before reading.");
        StopSpeech();
        var id = Guid.NewGuid().ToString("N"); speechId = id;
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); speechDone = done;
        Post(new { type = "speech", id, bundle, mood, paused=speechPaused, rate=speechRate });
        bool completed=false;
        try
        {
            // User pauses must not spend the playback watchdog. Slow speech uses
            // the longest supported rate; hidden/replaced/stop still finish immediately.
            double activeSeconds=0,limit=bundle.GetProperty("duration").GetDouble()/.65+20;
            var clock=System.Diagnostics.Stopwatch.StartNew();double previous=clock.Elapsed.TotalSeconds;
            while(!done.Task.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                await Task.WhenAny(done.Task,Task.Delay(100,token));
                double now=clock.Elapsed.TotalSeconds;
                if(!speechPaused)activeSeconds+=now-previous;
                previous=now;
                if(activeSeconds>limit)throw new TimeoutException("Speech playback did not finish.");
            }
            string reason = await done.Task.WaitAsync(token);
            if (reason is "stopped" or "paused" or "replaced") throw new OperationCanceledException("Speech playback stopped: " + reason);
            if (reason != "ended") throw new IOException("Audio playback failed: " + reason);
            completed=true;
        }
        finally { if(speechId==id) { Post(new { type="stop-speech",completed });speechId=null;speechDone=null; } }
    }
    public void StopSpeech() { Post(new { type="stop-speech" }); speechDone?.TrySetResult("stopped"); }
    public void ReactToSpeechStop() => Post(new { type="lip-stop-reaction" });
    public void Aim(double x, double y, bool tracking=true) => Post(new { type = "cursor", x, y, tracking });
    public async Task<string> InspectAsync(string script) => Ready ? await CoreWebView2.ExecuteScriptAsync(script) : "null";
    public void CloseAvatar() { disposed = true; Ready = false; Dispose(); }
}
