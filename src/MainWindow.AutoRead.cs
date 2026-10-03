using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace QuietGPT;

public partial class MainWindow
{
    private bool autoReadEnabled, autoReadPolling;
    private int autoReadEpoch;
    private readonly HashSet<string> autoReadHandled = [];
    private readonly DispatcherTimer autoReadTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private string AutoReadPath => Path.Combine(dataRoot, "auto-read.json");

    private async Task InitializeAutoRead()
    {
        try
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(AutoReadPath));
            autoReadEnabled = saved.RootElement.GetProperty("enabled").GetBoolean();
            foreach (var id in saved.RootElement.GetProperty("handled").EnumerateArray())
                if (id.GetString() is { } key) autoReadHandled.Add(key);
        }
        catch { }
        var core = Browser.CoreWebView2;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(AutoReadScript);
        core.NavigationStarting += (_, _) => { autoReadEpoch++; StopReading(); };
        core.SourceChanged += (_, _) => { autoReadEpoch++; StopReading(); };
        core.DOMContentLoaded += async (_, _) => {
            try { await core.ExecuteScriptAsync("window.__quietAutoRead?.enable(" + (autoReadEnabled ? "true" : "false") + ")"); } catch { }
        };
        autoReadTimer.Tick += async (_, _) => await PollAutoRead();
        autoReadTimer.Start();
    }

    private void SaveAutoRead()
    {
        try { File.WriteAllText(AutoReadPath, JsonSerializer.Serialize(new { enabled = autoReadEnabled, handled = autoReadHandled })); }
        catch { SetStatus("Could not save auto-read preferences"); }
    }

    private async void ToggleAutoRead()
    {
        autoReadEnabled = !autoReadEnabled; autoReadEpoch++; StopReading(); SaveAutoRead();
        try { await Browser.CoreWebView2.ExecuteScriptAsync("window.__quietAutoRead?.enable(" + (autoReadEnabled ? "true" : "false") + ")"); }
        catch { }
        SetStatus(autoReadEnabled ? "Auto-read on · Only replies to new prompts will be read" : "Auto-read off");
    }

    private async Task PollAutoRead()
    {
        if (closing) { autoReadTimer.Stop(); return; }
        if (!autoReadEnabled || autoReadPolling || Browser.CoreWebView2 is not { } core || !IsCompanionOrigin(core.Source)) return;
        autoReadPolling = true;
        int epoch = autoReadEpoch;
        try
        {
            var raw = await core.ExecuteScriptAsync("window.__quietAutoRead?.poll() ?? null");
            if (epoch != autoReadEpoch || !autoReadEnabled || closing) return;
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            var item = doc.RootElement;
            string path = item.GetProperty("path").GetString() ?? "";
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || uri.AbsolutePath != path) return;
            string id = item.GetProperty("id").GetString() ?? "";
            string text = item.GetProperty("text").GetString() ?? "";
            if (id.Length == 0 || text.Length == 0 || text.Length > 12000) return;
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "\n" + id)));
            if (!autoReadHandled.Add(key)) return;
            SaveAutoRead(); // Claim before playback: cancellation and restart must not replay it.
            if (reading != null) { SetStatus("Auto-read skipped · Speech is already playing"); return; }
            LoadVoicePreferences();
            await ReadAutomatically(text, epoch);
        }
        catch (Exception ex) { if (!closing) SetStatus("Auto-read could not continue: " + ex.Message); }
        finally { autoReadPolling = false; }
    }

    private async Task ReadAutomatically(string text, int epoch)
    {
        if (companion == null || epoch != autoReadEpoch) return;
        var cts = new CancellationTokenSource(); reading = cts;UpdateReadingControls();
        try { await SpeakText(text, speechEngine, speechReference, cts.Token, SetStatus); SetStatus("Auto-read finished · Waiting for your next prompt"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Auto-read failed · " + ex.Message); }
        finally { cts.Cancel(); if(reading==cts){reading=null;SetReadingPaused(false);} cts.Dispose();UpdateReadingControls(); }
    }

    // No audio or commands are accepted from the page. Native polling reads only
    // bounded reply text after a locally observed submission and completion.
    private const string AutoReadScript = """
    (() => {
      if(window!==top || !['chatgpt.com','quiet.test'].includes(location.hostname))return;
      let enabled=false, pending=null, path=location.pathname, pressedSend=null;
      const replies=()=>window.__quietReplyReader?.replies()||[...document.querySelectorAll('[data-message-author-role="assistant"]')];
      const id=n=>window.__quietReplyReader?.id(n)||n.getAttribute('data-message-id')||n.closest('[data-message-id]')?.getAttribute('data-message-id');
      const composerSelector='#prompt-textarea,[data-testid="prompt-textarea"],[data-composer-markdown][contenteditable="true"]';
      const visible=n=>n&&n.getClientRects().length>0;
      const busy=()=>window.__quietReplyReader?.busy()||[...document.querySelectorAll('[data-testid="stop-button"],button[aria-label="Stop generating"],button[aria-label="Stop streaming"]')].some(visible);
      function route(){
        if(path===location.pathname)return;
        // A first prompt creates its conversation URL without leaving the document.
        // The URL can arrive before React inserts the first user-message node.
        // An observed local submission is the anchor; link clicks/popstate still cancel it.
        const promoted=pending && /^\/(?:g\/[^/]+\/?)?$/.test(path) && location.pathname.includes('/c/');
        path=location.pathname;if(!promoted)pending=null;
      }
      function submitted(){
        route();if(!enabled)return;
        if(pending && Date.now()-pending.start<500)return;
        pending={old:new Set(replies().map(id)), start:Date.now(), busy:false, text:'', stable:0,
          users:[...document.querySelectorAll('[data-message-author-role="user"]')]};
      }
      document.addEventListener('submit',e=>{if(e.target.querySelector?.(composerSelector))submitted();},true);
      document.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey&&!e.isComposing&&e.target.closest?.(composerSelector))submitted();},true);
      const isSend=b=>b&&!b.disabled&&(b.matches('[data-testid="send-button"],[data-testid="composer-submit-button"],[data-composer-submit-button],button[aria-label="Send prompt"],button[aria-label="Send message"]')||(b.matches('button[aria-label="Send"]')&&!!b.closest('[data-composer-layout]')))&&!b.matches('[data-testid="stop-button"],button[aria-label="Stop generating"],button[aria-label="Stop streaming"]');
      // Capture before React can replace the send control with its stop state.
      document.addEventListener('pointerdown',e=>{
        pressedSend=null;const b=e.target.closest?.('button');
        if(e.button===0&&isSend(b)){pressedSend=b;submitted();}
      },true);
      document.addEventListener('click',e=>{
        if(e.target.closest?.('a[href]'))pending=null;
        const b=e.target.closest?.('button');if(!b)return;
        const wasSend=pressedSend===b;pressedSend=null;
        if(!wasSend&&isSend(b))submitted();
        if(!wasSend&&b.matches('[data-testid="stop-button"],button[aria-label="Stop generating"],button[aria-label="Stop streaming"],button[aria-label="Stop response"],[data-composer-layout] button[aria-label="Stop"]'))pending=null;
      },true);
      window.addEventListener('popstate',()=>{pending=null;path=location.pathname;});
      const observer=new MutationObserver(()=>{route();if(enabled&&pending&&busy())pending.busy=true;});
      function observe(){observer.observe(document.documentElement,{subtree:true,childList:true,attributes:true,attributeFilter:['data-testid','aria-label']});}
      if(document.documentElement)observe();else document.addEventListener('DOMContentLoaded',observe,{once:true});
      window.__quietAutoRead={
        enable(value){enabled=!!value;pending=null;pressedSend=null;path=location.pathname;},
        poll(){
          route();if(!enabled||!pending)return null;
          if(Date.now()-pending.start>1800000){pending=null;return null;}
          // User nodes appearing after submission also anchor a first-chat URL promotion.
          pending.users=[...document.querySelectorAll('[data-message-author-role="user"]')];
          if(busy()){pending.busy=true;pending.stable=0;return null;}
          const n=replies().filter(n=>id(n)&&!pending.old.has(id(n))).at(-1);if(!n)return null;
          const text=window.__quietReplyReader?window.__quietReplyReader.text(n):(()=>{const clone=(n.querySelector('.markdown')||n).cloneNode(true);clone.querySelectorAll('button,pre,script,style').forEach(x=>x.remove());return clone.textContent.trim().slice(0,12000);})();
          if(text!==pending.text){pending.text=text;pending.stable=Date.now();return null;}
          // Require a generation signal, then a quiet interval; hydration alone cannot trigger speech.
          const turn=n.closest('article,[data-testid^="conversation-turn"]')||n;
          const completed=window.__quietReplyReader?.completed(n)||!!turn.querySelector('[data-testid="copy-turn-action-button"]');
          if((!pending.busy&&!completed)||!text||Date.now()-pending.stable<2100)return null;
          const result={path:location.pathname,id:id(n),text};pending=null;return result;
        }
      };
    })();
    """;
}
