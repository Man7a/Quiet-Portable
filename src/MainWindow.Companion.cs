using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace QuietGPT;

public partial class MainWindow
{
    private CompanionWindow? companion;
    private string pageActivity = "unknown";
    private readonly DispatcherTimer companionReaction = new() { Interval = TimeSpan.FromSeconds(5) };

    private async Task InitializeCompanion(CoreWebView2 core)
    {
        companion = new CompanionWindow(dataRoot);
        if(!smokeTest)companion.ConnectToQuiet(this);
        companion.ReadReplyRequested += ReadReplyFromLips;
        companion.VisibilityPreferenceChanged += SyncCompanionObserver;
        companion.IsVisibleChanged += (_,_) => CompanionSpeechVisibilityChanged();
        CompanionSpeechVisibilityChanged();
        companionReaction.Tick += (_, _) => { companionReaction.Stop(); companion?.SetActivity(pageActivity); };
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += (_, e) =>
        {
            if (closing || !IsCompanionOrigin(e.Source)) return;
            try
            {
                // Page messages may choose a visual state only; never paths, commands or actions.
                using var json = JsonDocument.Parse(e.WebMessageAsJson);
                var value = json.RootElement;
                if (value.GetProperty("kind").GetString() != "quiet-activity") return;
                string? activity = value.GetProperty("state").GetString();
                if (activity is not ("idle" or "thinking" or "responding" or "done" or "unknown")) return;
                pageActivity = activity == "done" ? "idle" : activity;
                if (activity == "done") ReactCompanion("done");
                else if (!companionReaction.IsEnabled) companion?.SetActivity(activity);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ReplyReaderScript);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(CompanionObserverScript);
        core.DOMContentLoaded += (_, _) => SyncCompanionObserver();
        StartReplyDiagnostics(core);
        if (!smokeTest && companion.WantedVisible) companion.Reveal();
    }

    private bool IsCompanionOrigin(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Port == 443 && (uri.Host == "chatgpt.com" || (smokeTest && uri.Host == "quiet.test"));

    private async void SyncCompanionObserver()
    {
        if (closing || Browser.CoreWebView2 is not { } core || !IsCompanionOrigin(core.Source)) return;
        try { await core.ExecuteScriptAsync("window.__quietCompanionEnabled?.(" + (companion?.IsVisible == true || smokeTest ? "true" : "false") + ")"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void CompanionClick(object sender, RoutedEventArgs e)
    {
        if (companion == null) { SetStatus("Companion will be ready when the browser starts"); return; }
        if (companion.IsVisible) { StopReading(); companion.HideCompanion(); return; }
        companion.Reveal(true);
        if (companion.Collapsed) companion.SetCollapsed(false);
    }
    private void ReactCompanion(string state)
    {
        companion?.SetActivity(state); companionReaction.Stop(); companionReaction.Start();
    }

    private const string CompanionObserverScript = """
        (() => {
          if (window !== window.top || !['chatgpt.com', 'quiet.test'].includes(location.hostname)) return;
          let enabled = true, observer, scheduled = 0, submittedAt = 0, busyBefore = false;
          let lastAssistant = null, lastLength = 0, state = '', cancelRequested = false, expiry = 0;
          const visible = e => !!e && e.getClientRects().length > 0;
          const composer = () => document.querySelector('#prompt-textarea, [data-testid="prompt-textarea"], [data-composer-markdown][contenteditable="true"]');
          const send = next => {
            if (!enabled || next === state) return;
            state = next;
            window.chrome.webview.postMessage({kind: 'quiet-activity', state: next});
          };
          const stopButton = () => [...document.querySelectorAll('[data-testid="stop-button"], button[aria-label="Stop generating"], button[aria-label="Stop streaming"]')].find(visible);
          function inspect() {
            scheduled = 0;
            if (!enabled) return;
            const replies = window.__quietReplyReader?.replies()||document.querySelectorAll('[data-message-author-role="assistant"]');
            const last = replies.length ? replies[replies.length - 1] : null;
            const length = last?.textContent?.length || 0;
            const changed = last !== lastAssistant || length !== lastLength;
            const busy = window.__quietReplyReader?.busy()||!!stopButton();
            if (busy) {
              if (changed && last && (busyBefore || submittedAt)) send('responding');
              else if (!busyBefore) send('thinking');
              busyBefore = true;
            } else if (busyBefore) {
              busyBefore = false; submittedAt = 0;
              send(cancelRequested ? 'idle' : 'done'); cancelRequested = false;
            } else if (submittedAt && Date.now() - submittedAt < 30000) send('thinking');
            else { submittedAt = 0; send(visible(composer()) ? 'idle' : 'unknown'); }
            lastAssistant = last; lastLength = length;
          }
          function schedule() {
            if (enabled && !scheduled) scheduled = setTimeout(inspect, 200);
          }
          function submitted() {
            if (!enabled) return;
            submittedAt = Date.now(); cancelRequested = false; send('thinking');
            clearTimeout(expiry); expiry = setTimeout(schedule, 30100);
          }
          document.addEventListener('submit', event => {
            if (event.target?.contains?.(composer())) submitted();
          }, true);
          document.addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.shiftKey&&!event.isComposing&&event.target===composer())submitted();},true);
          document.addEventListener('click', event => {
            const button = event.target?.closest?.('button');
            if (!button || !enabled) return;
            if (button.matches('[data-testid="send-button"],[data-testid="composer-submit-button"],button[aria-label="Send prompt"],button[aria-label="Send message"],[data-composer-layout] button[aria-label="Send"]') && !button.disabled) submitted();
            if (button === stopButton()) cancelRequested = true;
          }, true);
          window.__quietCompanionEnabled = value => {
            enabled = !!value; clearTimeout(scheduled); scheduled = 0;
            observer?.disconnect();
            if (enabled && document.body) {
              state = ''; observer ??= new MutationObserver(schedule);
              observer.observe(document.body, {subtree:true, childList:true, characterData:true,
                attributes:true, attributeFilter:['data-testid','aria-label','disabled','hidden']});
              inspect();
            } else { clearTimeout(expiry); submittedAt = 0; busyBefore = false; }
          };
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', () => window.__quietCompanionEnabled(enabled), {once:true});
          else window.__quietCompanionEnabled(enabled);
        })();
        """;
}
