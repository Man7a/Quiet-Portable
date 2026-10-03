using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace QuietGPT;

public partial class MainWindow
{
    private AutoChatBridge? automaticBridge;
    private AutoChatBridgeWindow? automaticBridgeWindow;
    private bool automaticBridgeErrorShown;
    private readonly DispatcherTimer automaticBridgeTimer=new(){Interval=TimeSpan.FromSeconds(8)};
    private void InitializeAutomaticBridge()
    {
        if(smokeTest)return;
        try {
            automaticBridge=new AutoChatBridge(dataRoot,PrepareAutomaticReturn,CompleteAutomaticReturn);
            automaticBridgeTimer.Tick+=async(_,_)=>{
                if(closing){automaticBridgeTimer.Stop();return;}
                await automaticBridge.Tick();
                if(automaticBridge.Status.StartsWith("Connection needs attention",StringComparison.Ordinal)||
                   automaticBridge.Status.StartsWith("Bridge needs attention",StringComparison.Ordinal)||
                   automaticBridge.Status.StartsWith("Codex retry pending",StringComparison.Ordinal)){
                    SetStatus("Ray & Mira: "+automaticBridge.Status);automaticBridgeErrorShown=true;
                }else if(automaticBridgeErrorShown){SetStatus("Ready");automaticBridgeErrorShown=false;}
                await TryAutomaticSync();
            };
            automaticBridgeTimer.Start();Closed+=(_,_)=>{automaticBridgeTimer.Stop();automaticBridge.Dispose();};
        }catch(Exception ex){SetStatus("Chat bridge needs attention: "+ex.Message);}
    }
    private void ShowAutomaticBridge()
    {
        if(automaticBridgeWindow!=null){automaticBridgeWindow.Activate();return;}
        if(automaticBridge==null){MessageBox.Show(this,"The chat bridge could not load. Restart Quiet or check its saved configuration.","Ray & Mira");return;}
        automaticBridgeWindow=new AutoChatBridgeWindow(automaticBridge){Owner=this};automaticBridgeWindow.Closed+=(_,_)=>automaticBridgeWindow=null;automaticBridgeWindow.Show();
    }
    private async Task<bool> PrepareAutomaticReturn(AutoChatBridge.Job job)
    {
        var old=ReadBridgeNotice();
        if(old!=null&&old.RequestId!=job.Id&&bridgeVerifiedRequest!=old.RequestId)return false;
        var core=Browser.CoreWebView2;if(core==null||closing)return false;
        var n=new BridgeNotice(job.Id,job.RayId,"begin");
        var raw=await core.ExecuteScriptAsync("window.__quietBridge?.inspect("+JsonSerializer.Serialize(n)+") ?? null");
        using var doc=JsonDocument.Parse(raw);
        if(doc.RootElement.ValueKind!=JsonValueKind.Object||!doc.RootElement.GetProperty("safe").GetBoolean())return false;
        File.WriteAllText(BridgePath,JsonSerializer.Serialize(n));await PollRayBridge();
        // The gate must be applied before the external send, not just written to disk.
        raw=await core.ExecuteScriptAsync("window.__quietBridge?.update("+JsonSerializer.Serialize(n)+") ?? null");
        using var applied=JsonDocument.Parse(raw);return applied.RootElement.ValueKind==JsonValueKind.Object&&applied.RootElement.GetProperty("safe").GetBoolean();
    }
    private async Task CompleteAutomaticReturn(AutoChatBridge.Job job,string messageId)
    {
        if(!Guid.TryParse(messageId,out _))throw new InvalidDataException("Missing Ray reply ID");
        File.WriteAllText(BridgePath,JsonSerializer.Serialize(new BridgeNotice(job.Id,job.RayId,"completed",messageId)));
        await PollRayBridge();await TryAutomaticSync();
    }
    private async Task TryAutomaticSync()
    {
        var n=ReadBridgeNotice();
        if(n?.Phase!="completed"||automaticBridge==null||!automaticBridge.Jobs.Any(j=>j.Id==n.RequestId))return;
        if(bridgeReloading||bridgePolling||closing)return;
        // Existing sync guard atomically checks current chat, draft, uploads, generation and expected reply.
        BridgeSyncClick(this,new RoutedEventArgs());await Task.CompletedTask;
    }
}
