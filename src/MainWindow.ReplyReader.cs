using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace QuietGPT;

public partial class MainWindow
{
    private async Task<JsonElement> CollectCompletedReply(CoreWebView2 core)
    {
        // Also recover after a client-side document update removes the injected helper.
        await core.ExecuteScriptAsync(ReplyReaderScript);
        using var result=JsonDocument.Parse(await core.ExecuteScriptAsync("window.__quietReplyReader.latest()"));
        await CaptureReplyDiagnostics(core);
        return result.RootElement.Clone();
    }

    private async Task CaptureReplyDiagnostics(CoreWebView2 core)
    {
        if(smokeTest || closing || !IsCompanionOrigin(core.Source))return;
        try
        {
            var raw=await core.ExecuteScriptAsync("""
                (()=>{const api=window.__quietReplyReader,reply=api?.latest();return {
                host:location.hostname,conversationRoute:location.pathname.includes('/c/'),helper:!!api,
                state:reply?.state||'missing',replyLength:reply?.text?.length||0,
                assistantCount:api?.replies().length||0,
                modernAssistantCount:document.querySelectorAll('[data-markdown-text-style="assistant-message"]').length,
                composer:!!document.querySelector('[data-composer-markdown][contenteditable="true"],#prompt-textarea')};})()
                """);
            using var data=JsonDocument.Parse(raw);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dataRoot,"speech-page-diagnostic.json"),
                JsonSerializer.Serialize(new {timestampUtc=DateTimeOffset.UtcNow,page=data.RootElement.Clone()}));
        }
        catch { }
    }

    private async void StartReplyDiagnostics(CoreWebView2 core)
    {
        if(smokeTest)return;
        for(int i=0;i<24&&!closing;i++){await Task.Delay(5000);await CaptureReplyDiagnostics(core);}
    }

    private void RecordSpeechStage(string stage)
    {
        if(smokeTest)return;
        try{System.IO.File.WriteAllText(System.IO.Path.Combine(dataRoot,"speech-runtime-diagnostic.json"),JsonSerializer.Serialize(new {timestampUtc=DateTimeOffset.UtcNow,stage}));}catch{}
    }

    private const string ReplyReaderScript = """
    (()=>{
      if(window!==top||!['chatgpt.com','quiet.test'].includes(location.hostname)||window.__quietReplyReader)return;
      const visible=n=>!!n&&!n.hidden&&n.getClientRects().length>0;
      const identities=new WeakMap(),session=Date.now().toString(36)+'-'+Math.random().toString(36).slice(2);let serial=0;
      const authorSelector='[data-message-author-role="assistant"],[data-turn="assistant"],[data-role="assistant"],[data-testid="assistant-message"],[data-markdown-text-style="assistant-message"]';
      const userSelector='[data-message-author-role="user"],[data-turn="user"],[data-role="user"],[data-markdown-text-style="user-message"],[data-content-search-unit-key$=":user"],[data-chatgpt-search-unit-key$=":user"]';
      const actionSelector='[data-testid="copy-turn-action-button"],[data-testid="good-response-turn-action-button"],[data-testid="bad-response-turn-action-button"]';
      const contentSelector='.markdown,.prose,[data-message-content],[data-markdown-text-style="assistant-message"]';
      function replies(){
        const result=[...document.querySelectorAll(authorSelector)].filter(n=>visible(n)&&!n.closest(userSelector));
        for(const turn of document.querySelectorAll('article,[data-testid^="conversation-turn"]')){
          if(!visible(turn)||turn.matches(userSelector)||turn.querySelector(userSelector)||result.some(n=>turn===n||turn.contains(n)))continue;
          const content=turn.querySelector(contentSelector);
          if(content&&turn.querySelector(actionSelector))result.push(turn);
        }
        return [...new Set(result)].filter(n=>!result.some(other=>other!==n&&n.contains(other))).sort((a,b)=>a.compareDocumentPosition(b)&Node.DOCUMENT_POSITION_FOLLOWING?-1:1);
      }
      function id(node){
        const owner=node.closest('[data-message-id],[data-turn-id],[data-chatgpt-selection-message-id]');
        const explicit=node.getAttribute('data-message-id')||node.getAttribute('data-turn-id')||owner?.getAttribute('data-message-id')||owner?.getAttribute('data-turn-id')||owner?.getAttribute('data-chatgpt-selection-message-id');
        if(explicit)return explicit;
        if(!identities.has(node))identities.set(node,'quiet-dom-'+session+'-'+(++serial));
        return identities.get(node);
      }
      function text(node){
        const content=(node.matches(contentSelector)?node:node.querySelector(contentSelector)||node).cloneNode(true);
        content.querySelectorAll('button,pre,script,style,nav,[aria-hidden="true"]').forEach(n=>n.remove());
        content.querySelectorAll('br').forEach(n=>n.replaceWith(document.createTextNode('\n')));
        content.querySelectorAll('p,li,h1,h2,h3,h4,blockquote').forEach(n=>n.appendChild(document.createTextNode('\n')));
        return content.textContent.trim().slice(0,12000);
      }
      const busy=()=>[...document.querySelectorAll('[data-testid="stop-button"],button[aria-label="Stop generating"],button[aria-label="Stop streaming"],button[aria-label="Stop response"],[data-composer-layout] button[aria-label="Stop"]')].some(visible);
      const completed=node=>{
        const turn=node.closest('article,[data-testid^="conversation-turn"],[data-turn-key]')||node;
        return !!turn.querySelector(actionSelector)||(node.matches('[data-markdown-text-style="assistant-message"]')&&!!turn.querySelector('button[aria-label="Rate response"],button[aria-label="Regenerate response"],button[aria-label="Read aloud"]'));
      };
      window.__quietReplyReader={replies,id,text,busy,completed,latest(){
        if(busy())return {state:'streaming',text:''};
        const node=replies().at(-1),value=node?text(node):'';
        return {state:value?'ready':'empty',text:value};
      }};
    })();
    """;
}
