using System.IO;
using System.Text.Json;

namespace QuietGPT;

public partial class MainWindow
{
    private RayMiraWindow? rayMiraWindow;
    private void ShowRayMira()
    {
        if(rayMiraWindow is {} existing){existing.Activate();return;}
        var store=new RayMiraStore(Path.Combine(dataRoot,"RayMira"));
        rayMiraWindow=new RayMiraWindow(store,ImportRayReply){Owner=this};
        rayMiraWindow.Closed+=(_,_)=>rayMiraWindow=null;rayMiraWindow.Show();
    }
    private async Task<string> ImportRayReply()
    {
        var core=Browser.CoreWebView2;
        if(core is null)throw new InvalidOperationException("Open Workbench in Quiet first.");
        var raw=await core.ExecuteScriptAsync(RayImportScript);
        using var doc=JsonDocument.Parse(raw);var d=doc.RootElement;
        if(d.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Could not read this page.");
        if(d.TryGetProperty("error",out var error))throw new InvalidOperationException(error.GetString());
        if(!Uri.TryCreate(core.Source,UriKind.Absolute,out var uri)||uri.Host!="chatgpt.com"||!uri.AbsolutePath.EndsWith("/c/"+RayMiraStore.RayId))throw new InvalidOperationException("The chat changed. Open Workbench and import again.");
        return d.GetProperty("text").GetString()??"";
    }
    internal const string RayImportScript="""
    (()=>{
      if(location.hostname!=='chatgpt.com' || location.pathname!=='/c/00000000-0000-0000-0000-000000000001') return {error:'Open Ray & Mira — Workbench in Quiet first. Other chats are not imported.'};
      if(document.querySelector('[data-testid="stop-button"],button[aria-label="Stop generating"],[data-is-streaming="true"]'))return {error:'Wait for Ray to finish her reply.'};
      const reply=[...document.querySelectorAll('[data-message-author-role="assistant"]')].at(-1);
      if(!reply)return {error:'No reply from Ray is visible yet.'};
      const turn=reply.closest('article,[data-testid^="conversation-turn-"]')||reply.parentElement;
      if(!turn?.querySelector('[data-testid="copy-turn-action-button"],button[aria-label="Copy response"]'))return {error:'The reply is not confirmed complete yet. Wait a moment and try again.'};
      const copy=reply.cloneNode(true);copy.querySelectorAll('button,script,style,svg').forEach(e=>e.remove());
      copy.querySelectorAll('p,li,pre,h1,h2,h3,blockquote').forEach(e=>e.append('\n'));
      const text=copy.textContent.trim();if(!text)return {error:'This reply contains no text to import.'};
      if(text.length>24000)return {error:'This reply is too long. Paste only the focused request into the panel.'};
      return {text};
    })()
    """;
}
