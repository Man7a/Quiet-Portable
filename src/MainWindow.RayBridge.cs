using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace QuietGPT;

public partial class MainWindow
{
    private const string RayConversation = "00000000-0000-0000-0000-000000000001";
    private readonly DispatcherTimer bridgeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool bridgePolling, bridgeReloading;
    private readonly HashSet<string> bridgeAutomaticReloads=[];
    private string? bridgeVerifiedRequest;
    private string BridgePath => Path.Combine(dataRoot, "ray-bridge.json");
    private sealed record BridgeNotice(string RequestId, string ConversationId, string Phase, string? MessageId = null);

    private BridgeNotice? ReadBridgeNotice()
    {
        try
        {
            if (new FileInfo(BridgePath).Length > 4096) return null;
            var n = JsonSerializer.Deserialize<BridgeNotice>(File.ReadAllText(BridgePath));
            return n is not null && Guid.TryParse(n.RequestId, out _) && (n.ConversationId == RayConversation || automaticBridge?.Bindings.Any(b=>b.RayId==n.ConversationId)==true)
                && (n.Phase == "begin" || n.Phase == "completed")
                && (n.Phase != "completed" || Guid.TryParse(n.MessageId, out _)) ? n : null;
        }
        catch { return null; }
    }

    private string StartupAddress()
    {
        // A reply that arrived while Quiet was closed belongs in its Ray chat,
        // not on the ChatGPT home page where the user cannot see it.
        var notice=ReadBridgeNotice();
        if(notice?.Phase!="completed")return Home;
        try {
            using var ack=JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot,"ray-bridge-ack.json")));
            if(ack.RootElement.GetProperty("RequestId").GetString()==notice.RequestId&&ack.RootElement.GetProperty("Synced").GetBoolean())return Home;
        }catch { }
        return Home+"c/"+notice.ConversationId;
    }

    private async Task InitializeRayBridge()
    {
        try {
            using var ack = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "ray-bridge-ack.json")));
            if (ack.RootElement.GetProperty("Synced").GetBoolean()) bridgeVerifiedRequest = ack.RootElement.GetProperty("RequestId").GetString();
        } catch { }
        var core = Browser.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
        core.NavigationCompleted += async (_, _) => { bridgeReloading = false; await PollRayBridge(); };
        bridgeTimer.Tick += async (_, _) => await PollRayBridge();
        bridgeTimer.Start();
    }

    private async Task PollRayBridge()
    {
        if (closing) { bridgeTimer.Stop(); return; }
        if (bridgePolling || bridgeReloading || Browser.CoreWebView2 is not { } core) return;
        bridgePolling = true;
        try
        {
            var n = ReadBridgeNotice();
            // A completed, verified return must not leave an old page lock in place.
            if(n?.Phase=="completed"&&bridgeVerifiedRequest==n.RequestId&&automaticBridge?.Jobs.Any(j=>j.Id==n.RequestId&&j.State=="done")==true){
                File.Delete(BridgePath);n=null;
            }
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || (uri.Host != "chatgpt.com" && !(smokeTest && uri.Host == "quiet.test")))
            { BridgeBar.Visibility = Visibility.Collapsed; return; }
            var raw = await core.ExecuteScriptAsync("window.__quietBridge?.update(" + JsonSerializer.Serialize(n) + ") ?? null");
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var state = doc.RootElement;
            bool active = state.GetProperty("active").GetBoolean();
            bool safe = state.GetProperty("safe").GetBoolean();
            bool found = state.GetProperty("found").GetBoolean();
            bool editing = state.GetProperty("editing").GetBoolean();
            if (found) bridgeVerifiedRequest = n?.RequestId;
            if (n is null) { BridgeBar.Visibility = Visibility.Collapsed; return; }
            // Sender must wait for this acknowledgement before using the chat tool.
            File.WriteAllText(Path.Combine(dataRoot, "ray-bridge-ack.json"), JsonSerializer.Serialize(new {
                n.RequestId, n.Phase, Ready = !active || safe, Synced = bridgeVerifiedRequest == n.RequestId, At = DateTimeOffset.UtcNow
            }));
            // Even an overlay becoming visible can shift keyboard focus in WebView2.
            // The page gate is already active; wait until editing ends to show the banner.
            if (editing && active) { if (BridgeSync.IsEnabled) BridgeSync.IsEnabled = false; return; }
            var barVisibility = active && !found ? Visibility.Visible : Visibility.Collapsed;
            if (BridgeBar.Visibility != barVisibility) BridgeBar.Visibility = barVisibility;
            var syncEnabled = active && n.Phase == "completed" && safe;
            if (BridgeSync.IsEnabled != syncEnabled) BridgeSync.IsEnabled = syncEnabled;
            var message = n.Phase == "begin"
                ? (safe ? "Quiet is delivering a bridge message · Sending here is paused" : "Bridge waiting · Finish typing, then click outside the message box")
                : (safe ? "New messages from Ray · Sync before sending" : "Reply ready · Finish typing, then click outside the message box to sync");
            if (BridgeText.Text != message) BridgeText.Text = message;
        }
        catch { /* Keep pending state on disk; a page error must never authorize a send. */ }
        finally { bridgePolling = false; }
    }

    private async void BridgeSyncClick(object sender, RoutedEventArgs e)
    {
        if (bridgePolling || bridgeReloading || ReadBridgeNotice() is not { Phase: "completed" } n) return;
        bool automatic=ReferenceEquals(sender,this);
        if(automatic&&bridgeAutomaticReloads.Contains(n.RequestId))return;
        var core = Browser.CoreWebView2;
        bridgeReloading = true;
        try
        {
            // Freeze editing within the same JS task as the final safety check.
            var result = await core.ExecuteScriptAsync("window.__quietBridge?.prepare(" + JsonSerializer.Serialize(n) + ") ?? false");
            if (result != "true") { bridgeReloading = false; await PollRayBridge(); return; }
            bridgeReloading = true;
            if(automatic)bridgeAutomaticReloads.Add(n.RequestId);
            core.Reload();
        }
        catch { bridgeReloading = false; await PollRayBridge(); }
    }

    private const string BridgeScript = """
    (() => {
      if (window !== top || !['chatgpt.com','quiet.test'].includes(location.hostname) || window.__quietBridge) return;
      let notice = null, frozen = false, initializing = true, lastEdit = 0;
      const route = () => location.pathname.match(/\/c\/([^/]+)/)?.[1];
      const composer = () => document.querySelector('#prompt-textarea, [data-testid="prompt-textarea"], [contenteditable="true"][data-placeholder], textarea');
      const editing = () => {
        const a = document.activeElement;
        return !!a && (a.matches?.('input,textarea,[contenteditable="true"],[role="textbox"]') ||
          !!a.closest?.('[contenteditable="true"],[role="textbox"]') || !!composer()?.contains(a));
      };
      window.addEventListener('beforeinput', e => {
        if (composer()?.contains(e.target)) lastEdit = Date.now();
      }, true);
      window.addEventListener('compositionstart', e => {
        if (composer()?.contains(e.target)) lastEdit = Date.now();
      }, true);
      const busy = () => !!document.querySelector('[data-testid="stop-button"], button[aria-label="Stop generating"], [data-is-streaming="true"]');
      const draft = () => {
        const c = composer();
        if (!c) return true; // Unknown layout: fail closed.
        if ((c.value ?? c.textContent ?? '').trim()) return true;
        const area = c.closest('form') || c.parentElement?.parentElement;
        // Decorative images can live inside ChatGPT's composer. Only upload
        // controls/previews (or actual selected files) indicate a draft.
        return !area || !!area.querySelector('[role="progressbar"], [data-testid*="attachment"], [data-testid*="upload"], [aria-label*="Remove attachment"], [aria-label*="Remove file"], [aria-label*="remove attachment"], [aria-label*="remove file"]') ||
          [...document.querySelectorAll('input[type="file"]')].some(i => i.files?.length);
      };
      const state = () => {
        const active = !!notice && route() === notice.ConversationId;
        const found = active && notice.Phase === 'completed' && [...document.querySelectorAll('[data-message-id]')].some(e => e.dataset.messageId === notice.MessageId);
        const isEditing = editing();
        return {active, found, editing:isEditing, safe: !active || (!busy() && !draft() && !isEditing && Date.now() - lastEdit > 5000)};
      };
      const locked = () => { const s = state(); return initializing || (s.active && !s.found); };
      const stop = e => { e.preventDefault(); e.stopImmediatePropagation(); };
      for (const type of ['submit','keydown','click','pointerdown','paste','drop']) {
        window.addEventListener(type, e => {
          if (frozen) { stop(e); return; }
          if (!locked()) return;
          const send = e.target.closest?.('[data-testid="send-button"], [data-testid="composer-submit-button"]:not([aria-label*="Stop"]), button[aria-label="Send prompt"], button[aria-label="Send message"]');
          if (type === 'submit' || send || (type === 'keydown' && e.key === 'Enter' && !e.shiftKey && composer()?.contains(e.target))) stop(e);
        }, true);
      }
      window.addEventListener('beforeinput', e => { if (frozen) stop(e); }, true);
      window.__quietBridge = {
        inspect(n) { const previous=notice; notice=n; const s=state(); notice=previous; return s; },
        update(n) { notice = n; initializing=false; frozen = false; return state(); },
        prepare(n) { notice = n; const s = state(); frozen = s.active && s.safe && !s.found && n.Phase === 'completed'; return frozen; }
      };
    })();
    """;
}
