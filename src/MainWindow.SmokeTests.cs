using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QuietGPT;

public partial class MainWindow
{
    // Diagnostic mode uses its own dataRoot; it never signs in or reads the user's profile.
    private async Task RunSmokeTestsAsync()
    {
        var results = new List<object>();
        int failures = 0;
        string output = Path.Combine(AppContext.BaseDirectory, "test-results");
        string fixtures = Path.Combine(dataRoot, "fixtures");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(fixtures);
        await File.WriteAllTextAsync(Path.Combine(fixtures, "index.html"), """
            <!doctype html><html><head><title>Quiet diagnostic</title></head>
            <body><h1>Quiet diagnostic</h1><a id="next" target="_blank" href="https://quiet.test/next.html">Next</a>
            <form onsubmit="event.preventDefault()"><textarea id="prompt-textarea"></textarea><button data-testid="send-button">Send</button></form>
            <script>function downloadFixture(navigate){const a=document.createElement('a');
            a.href=URL.createObjectURL(new Blob(['Quiet download verified.\n'],{type:'application/octet-stream'}));
            if(navigate){location.href=a.href;return;}
            a.download='quiet-smoke.txt';document.body.appendChild(a);a.click();}</script></body></html>
            """);
        await File.WriteAllTextAsync(Path.Combine(fixtures, "next.html"),
            "<!doctype html><title>Quiet next page</title><h1>Same window verified</h1>");
        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("quiet.test", fixtures, CoreWebView2HostResourceAccessKind.DenyCors);

        async Task Check(string name, Func<Task<object>> action)
        {
            try { results.Add(new { name, passed = true, details = await action().WaitAsync(TimeSpan.FromSeconds(name=="Installed local voice worker"||name.Contains("actual Turbo speech")?75:30)) }); }
            catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.Message }); }
        }

        if(Environment.GetCommandLineArgs().Contains("--portable-relocation-test"))
        {
            await Check("Relocated portable folder: saved sample, artwork and actual Turbo speech",async()=>
            {
                LoadVoicePreferences();
                SmokeAssert((speechReference==PortablePaths.DefaultVoice||speechReference.StartsWith(dataRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))&&File.Exists(speechReference),"Saved voice selection did not resolve after moving the folder");
                SmokeAssert(File.Exists(PortablePaths.LoadReference("Voices/relocation.wav")),"Custom voice sample did not resolve after moving the folder");
                companion!.UseRiggedAvatar(false);companion.Reveal(false);companion.SetCollapsed(false);
                for(int n=0;n<100&&!companion.IsRiggedAvatarReady;n++)await Task.Delay(100);
                SmokeAssert(companion.IsRiggedAvatarReady,"Relocated artwork did not load");
                SmokeAssert(PortablePaths.VoiceReady,"Relocated voice runtime did not resolve");
                SmokeAssert(File.Exists(PortablePaths.LoadReference("@bundled/F_Quiet.wav")),"Bundled Quiet voice did not resolve after moving the folder");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var result=await localSpeech.Generate("Hello there. This folder moved, and my voice still works.","chatterbox-turbo",PortablePaths.DefaultVoice,timeout.Token);
                var bundle=result.GetProperty("bundle");SmokeAssert(bundle.GetProperty("cues").GetArrayLength()>0,"Relocated voice lost mouth timing");
                await companion.InspectRiggedAvatarAsync("voice.muted=true;true");
                await companion.PlaySpeech(bundle,timeout.Token);
                return new{relativeSample=true,artwork=true,voice=true,mouthTiming=true};
            });
            await File.WriteAllTextAsync(Path.Combine(output,"relocation-results.json"),JsonSerializer.Serialize(new{passed=failures==0,failures,checks=results},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(failures==0?0:1);return;
        }

        await Check("Portable profile, voice setup and Turbo-only defaults", async () =>
        {
            SmokeAssert(dataRoot==PortablePaths.Data&&dataRoot.StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase),"Profile escaped the portable folder");
            SmokeAssert(PortablePaths.InstanceName(false).StartsWith("Local\\QuietPortable.")&&PortablePaths.InstanceName(false)!=PortablePaths.InstanceName(true),"Portable instance is not isolated");
            SmokeAssert(SpeechEngines.SequenceEqual(new[]{"chatterbox-turbo"})&&speechReference==PortablePaths.DefaultVoice&&File.Exists(speechReference),"Portable default Quiet voice is missing or another engine was retained");
            SmokeAssert(PortablePaths.LoadReference(PortablePaths.SaveReference(speechReference))==PortablePaths.DefaultVoice,"Bundled voice reference is not portable");
            var source=Path.Combine(fixtures,"portable-sample.wav");await File.WriteAllBytesAsync(source,new byte[]{1,2,3});
            var imported=PortablePaths.ImportVoice(source);var relative=PortablePaths.SaveReference(imported);
            SmokeAssert(!Path.IsPathRooted(relative)&&File.Exists(PortablePaths.LoadReference(relative)),"Voice sample would not survive a folder move");File.Delete(imported);
            OpenVoiceSetup();await Task.Delay(150);
            SmokeAssert(voiceSetupWindow!=null&&SmokeControls<System.Windows.Controls.Button>(voiceSetupWindow).Any(b=>b.Tag?.ToString()=="install-voice"),"Voice installer is not reachable");
            CaptureElement(voiceSetupWindow!,Path.Combine(output,"portable-voice-setup.png"));voiceSetupWindow!.Close();
            return new{isolatedProfile=true,turboOnly=true,bundledQuietVoice=true,relativeVoiceSample=true,setupAvailable=true};
        });

        await Check("Launching again recovers hidden and minimized windows", async () =>
        {
            var handle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var oldState=WindowState;
            async Task Relaunch()
            {
                using var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!,"--smoke-test") { UseShellExecute=false,CreateNoWindow=true });
                SmokeAssert(process!=null,"Recovery process did not start");
                await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                SmokeAssert(process.ExitCode==0,"Recovery process failed");
                await Task.Delay(250);
            }
            try
            {
                Hide();
                SmokeAssert(!App.IsWindowVisible(handle),"Window did not hide for test");
                SmokeAssert(App.FindMainWindow(Environment.ProcessId)==handle,"Hidden main window not found");
                await Relaunch();
                SmokeAssert(App.IsWindowVisible(handle),"Repeated launch did not restore hidden window");
                Show();WindowState=WindowState.Minimized;
                await Relaunch();
                SmokeAssert(WindowState!=WindowState.Minimized,"Repeated launch did not restore minimized window");
            }
            finally { Show();WindowState=oldState; }
            return new { hiddenRecovery=true,minimizedRecovery=true };
        });

        await Check("Ray bridge protects drafts, locks only its chat and verifies reply IDs", async () =>
        {
            await SmokeNavigateAsync(Browser.CoreWebView2, "https://quiet.test/index.html");
            Browser.Focus();
            double browserHeight=Browser.ActualHeight;
            var raw = await Browser.CoreWebView2.ExecuteScriptAsync("""
              (() => {
                const a=window.__quietBridge, c=document.querySelector('#prompt-textarea'), flags={};
                const n={ConversationId:'00000000-0000-0000-0000-000000000001',Phase:'begin'};
                history.replaceState({},'', '/c/'+n.ConversationId);
                flags.empty=a.update(n).safe;
                const enter=new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true});c.dispatchEvent(enter);flags.locked=enter.defaultPrevented;
                c.value='Do not lose me';flags.draft=!a.update(n).safe;
                n.Phase='completed';n.MessageId='expected';flags.noRefresh=!a.prepare(n)&&c.value==='Do not lose me';c.value='';
                const img=document.createElement('img');c.parentElement.append(img);flags.decorativeImageSafe=a.update(n).safe;img.remove();
                const attachment=document.createElement('div');attachment.dataset.testid='attachment-preview';c.parentElement.append(attachment);flags.upload=!a.update(n).safe;attachment.remove();
                const stop=document.createElement('button');stop.dataset.testid='stop-button';document.body.append(stop);flags.busy=!a.update(n).safe;stop.remove();
                c.focus();flags.focusedEmptyProtected=!a.prepare(n);
                a.update(n);flags.pollPreservesFocus=document.activeElement===c;
                c.blur();flags.safeRefresh=a.prepare(n);a.update(n);
                const reply=document.createElement('div');reply.dataset.messageId='wrong';document.body.append(reply);flags.wrong=!a.update(n).found;
                reply.dataset.messageId='expected';flags.verified=a.update(n).found;reply.remove();
                history.replaceState({},'', '/c/other');flags.other=!a.update(n).active;
                const key=new KeyboardEvent('keydown',{key:'Enter',bubbles:true,cancelable:true});c.dispatchEvent(key);flags.otherUnlocked=!key.defaultPrevented;
                a.update(null);history.replaceState({},'', '/index.html');return flags;
              })()
              """);
            using var doc=JsonDocument.Parse(raw);
            foreach(var flag in doc.RootElement.EnumerateObject()) SmokeAssert(flag.Value.GetBoolean(),flag.Name);
            UpdateLayout();
            SmokeAssert(Math.Abs(Browser.ActualHeight-browserHeight)<1,"Bridge banner resized the browser while the user was typing");
            return raw;
        });

        await Check("Ray notification handshake and sync banner", async () =>
        {
            bridgeTimer.Stop();
            var n = new BridgeNotice(Guid.NewGuid().ToString(), RayConversation, "begin");
            try {
                await Browser.CoreWebView2.ExecuteScriptAsync("history.replaceState({},'', '/c/" + RayConversation + "')");
                double heightBefore=Browser.ActualHeight;
                await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#prompt-textarea').focus()");
                File.WriteAllText(BridgePath, JsonSerializer.Serialize(n));
                await PollRayBridge();
                if(!File.Exists(Path.Combine(dataRoot,"ray-bridge-ack.json"))){await Task.Delay(150);await PollRayBridge();}
                SmokeAssert(BridgeBar.Visibility==Visibility.Collapsed,"Polling displayed the banner over an active composer");
                SmokeAssert(await Browser.CoreWebView2.ExecuteScriptAsync("document.activeElement===document.querySelector('#prompt-textarea')") == "true","Polling stole composer focus");
                await Browser.CoreWebView2.ExecuteScriptAsync("document.activeElement.blur()");
                // A preceding fixture intentionally emits beforeinput. Honor the
                // real bridge's five-second typing cooldown rather than assuming it expired.
                for(int attempt=0;attempt<130;attempt++)
                {
                    await Task.Delay(50);await PollRayBridge();
                    using var ready=JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot,"ray-bridge-ack.json")));
                    if(ready.RootElement.GetProperty("Ready").GetBoolean())break;
                }
                UpdateLayout();
                SmokeAssert(Math.Abs(Browser.ActualHeight-heightBefore)<1,"Bridge banner resized the browser");
                using(var ack=JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot,"ray-bridge-ack.json"))))
                    SmokeAssert(ack.RootElement.GetProperty("Ready").GetBoolean(),"No ready acknowledgement: "+await Browser.CoreWebView2.ExecuteScriptAsync("window.__quietBridge.inspect("+JsonSerializer.Serialize(n)+")"));
                SmokeAssert(BridgeBar.Visibility==Visibility.Visible && !BridgeSync.IsEnabled,"Consulting banner missing");
                n=n with {Phase="completed",MessageId=Guid.NewGuid().ToString()};
                File.WriteAllText(BridgePath,JsonSerializer.Serialize(n));
                SmokeAssert(StartupAddress()=="https://chatgpt.com/c/"+n.ConversationId,"Startup lost a pending Ray reply");
                await PollRayBridge();
                SmokeAssert(BridgeSync.IsEnabled,"Sync not available on empty composer");
                await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#prompt-textarea').focus();document.querySelector('#prompt-textarea').dispatchEvent(new InputEvent('beforeinput',{bubbles:true}))");
                await PollRayBridge();
                SmokeAssert(!BridgeSync.IsEnabled,"Sync enabled while the composer was focused");
                int protectedRefreshes=0;
                EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs> protect=(_,_)=>protectedRefreshes++;
                Browser.CoreWebView2.NavigationStarting+=protect;
                try {BridgeSyncClick(this,new RoutedEventArgs());await Task.Delay(300);SmokeAssert(protectedRefreshes==0,"Automatic sync navigated while typing");}
                finally {Browser.CoreWebView2.NavigationStarting-=protect;}
                SmokeAssert(await Browser.CoreWebView2.ExecuteScriptAsync("document.activeElement===document.querySelector('#prompt-textarea')") == "true","Bridge poll or sync stole composer focus");
                await Task.Delay(100);CaptureElement(BridgeBar,Path.Combine(output,"ray-bridge-banner.png"));
                await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#prompt-textarea').value='Saved draft'");
                await PollRayBridge();SmokeAssert(!BridgeSync.IsEnabled,"Sync enabled with a draft");
                await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#prompt-textarea').value='';history.replaceState({},'', '/c/other')");
                await PollRayBridge();SmokeAssert(BridgeBar.Visibility==Visibility.Collapsed,"Other chat was affected");
                await Browser.CoreWebView2.ExecuteScriptAsync("history.replaceState({},'', '/c/"+RayConversation+"');document.activeElement?.blur()");
                await Task.Delay(5100); // The recent-edit guard must expire before the deliberate refresh fixture.
                int refreshes=0;
                EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs> countRefresh=(_,_)=>refreshes++;
                Browser.CoreWebView2.NavigationStarting+=countRefresh;
                try {
                    BridgeSyncClick(this,new RoutedEventArgs());await Task.Delay(700);
                    SmokeAssert(refreshes==1,"First automatic sync did not reload");
                    BridgeSyncClick(this,new RoutedEventArgs());await Task.Delay(300);
                    SmokeAssert(refreshes==1,"Missing reply caused repeated automatic reloads");
                } finally {Browser.CoreWebView2.NavigationStarting-=countRefresh;}
                return new { handshake=true,banner=true,draftProtected=true,otherChatUnaffected=true,noRefreshLoop=true };
            } finally {
                File.Delete(BridgePath);File.Delete(Path.Combine(dataRoot,"ray-bridge-ack.json"));
                await Browser.CoreWebView2.ExecuteScriptAsync("window.__quietBridge.update(null);history.replaceState({},'', '/index.html')");
                BridgeBar.Visibility=Visibility.Collapsed;bridgeTimer.Start();
            }
        });

        await Check("Ray panel stores approvals and rejects duplicate or mismatched results", async () =>
        {
            var folder=Path.Combine(dataRoot,"ray-panel-"+Guid.NewGuid().ToString("N"));
            var store=new RayMiraStore(folder);
            SmokeAssert(!store.Settings.Enabled,"Connection must start off");
            bool denied=false;try{store.Prepare("Test");}catch(InvalidOperationException){denied=true;}SmokeAssert(denied,"Off connection prepared a request");
            store.Save(true,"Review this small test request.");
            var request=store.Prepare(store.Settings.Draft);
            SmokeAssert(store.Current()==null,"Preparing a review approved the request");
            SmokeAssert(request.Prompt.Contains(request.Text)&&request.Prompt.Contains(RayMiraStore.CodexId),"Reviewed text or target missing");
            store.Approve(request);
            denied=false;try{store.Prepare("Duplicate");}catch(InvalidOperationException){denied=true;}SmokeAssert(denied,"Duplicate handoff accepted");
            var restored=new RayMiraStore(folder);SmokeAssert(restored.Current()?.RequestId==request.RequestId&&restored.Settings.Draft==store.Settings.Draft,"Restart lost request or draft");
            string resultPath=Path.Combine(folder,request.RequestId+".result.json");
            File.WriteAllText(resultPath,JsonSerializer.Serialize(new RayMiraStore.Result(Guid.NewGuid().ToString(),RayMiraStore.RayId,RayMiraStore.CodexId,"completed","Wrong job")));
            denied=false;try{restored.ReadResult();}catch(InvalidDataException){denied=true;}SmokeAssert(denied,"Wrong-job result accepted");
            File.WriteAllText(resultPath,JsonSerializer.Serialize(new RayMiraStore.Result(request.RequestId,RayMiraStore.RayId,RayMiraStore.CodexId,"completed","Preview test: the approved request was processed. No live prompt was sent.")));
            SmokeAssert(restored.ReadResult()?.Status=="completed","Matching result rejected");
            var panel=new RayMiraWindow(restored,()=>Task.FromResult("Unused")){Owner=this};
            try {
                panel.Show();panel.Tabs.SelectedIndex=1;panel.RefreshState();await Task.Delay(150);
                SmokeAssert(panel.ReturnButton.IsEnabled&&panel.StateText.Text=="Result ready for review","Result UI state incorrect");
                CaptureElement(panel,Path.Combine(output,"ray-mira-result.png"));
                panel.EnabledBox.IsChecked=false;panel.EnabledBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                SmokeAssert(!panel.ReturnButton.IsEnabled&&!panel.ReopenButton.IsEnabled,"Off connection permits handoffs");
                restored.CloseHandoff();panel.RequestBox.Text="Ask Mira to compare two simple approaches before implementing anything.";panel.Tabs.SelectedIndex=0;panel.RefreshState();await Task.Delay(100);
                CaptureElement(panel,Path.Combine(output,"ray-mira-panel.png"));
            } finally {panel.Close();}
            return new {defaultOff=true,explicitApproval=true,restart=true,duplicateBlocked=true,resultCorrelation=true,offBlocksCopy=true};
        });

        await Check("Ray import only reads a completed Workbench reply and preserves code", async () =>
        {
            await SmokeNavigateAsync(Browser.CoreWebView2,"https://quiet.test/index.html");
            string script=RayImportScript.Replace("location.hostname!=='chatgpt.com'","location.hostname!=='quiet.test'");
            var first=await Browser.CoreWebView2.ExecuteScriptAsync(script);SmokeAssert(first.Contains("Other chats"),"Other chat was imported");
            await Browser.CoreWebView2.ExecuteScriptAsync("history.replaceState({},'', '/c/"+RayMiraStore.RayId+"');document.body.insertAdjacentHTML('beforeend', '<article><div data-message-author-role=assistant><p>Compare approaches.</p><pre>const x = 2;</pre></div></article>')");
            var pending=await Browser.CoreWebView2.ExecuteScriptAsync(script);SmokeAssert(pending.Contains("not confirmed complete"),"Unfinished reply imported");
            await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('article').insertAdjacentHTML('beforeend','<button data-testid=copy-turn-action-button>Copy</button>')");
            var imported=await Browser.CoreWebView2.ExecuteScriptAsync(script);SmokeAssert(imported.Contains("const x = 2;")&&imported.Contains("Compare approaches."),"Code lost during import");
            await Browser.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend','<button data-testid=stop-button>Stop</button>')");
            var busy=await Browser.CoreWebView2.ExecuteScriptAsync(script);SmokeAssert(busy.Contains("Wait for Ray"),"Streaming reply imported");
            await SmokeNavigateAsync(Browser.CoreWebView2,"https://quiet.test/index.html");
            return new {scoped=true,completionChecked=true,codePreserved=true,streamingBlocked=true};
        });

        await Check("Automatic multi-chat bridge queues and returns exactly once", () => TestAutomaticBridge(output));

        await Check("Auto-read ignores history and follows only newly completed replies", async () =>
        {
            var core=Browser.CoreWebView2;
            core.Navigate("https://quiet.test/index.html");
            for(int i=0;i<100;i++){await Task.Delay(50);if(await core.ExecuteScriptAsync("!!document.querySelector('#prompt-textarea')")=="true")break;}
            await core.ExecuteScriptAsync(AutoReadScript);
            var raw=await core.ExecuteScriptAsync("""
              (()=>{
                const api=window.__quietAutoRead, flags={};
                let clock=Date.now();const originalNow=Date.now;Date.now=()=>clock;
                const add=(id,text)=>{const n=document.createElement('div');n.dataset.messageAuthorRole='assistant';n.dataset.messageId=id;n.innerHTML='<div class="markdown"></div>';n.firstChild.textContent=text;document.body.append(n);return n;};
                const submit=()=>document.querySelector('[data-testid="send-button"]').click();
                const busy=()=>{const b=document.createElement('button');b.dataset.testid='stop-button';b.textContent='Stop';document.body.append(b);api.poll();return b;};
                const complete=()=>{api.poll();clock+=2500;return api.poll();};
                try{
                  add('old','Old reply');api.enable(true);flags.baseline=api.poll()===null;
                  add('history','Hydrated history');flags.hydration=complete()===null;
                  submit();const b=busy();add('new','Fresh response');flags.streaming=api.poll()===null;b.remove();
                  const got=complete();flags.newReply=got?.id==='new'&&got.text==='Fresh response';flags.once=api.poll()===null;
                  clock+=1000;submit();const quick=add('quick','Quick answer');const copy=document.createElement('button');copy.dataset.testid='copy-turn-action-button';quick.append(copy);flags.fastCompletion=complete()?.id==='quick';
                  clock+=1000;const morph=document.createElement('button');morph.dataset.testid='composer-submit-button';morph.setAttribute('aria-label','Send prompt');document.body.append(morph);
                  morph.dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,button:0}));morph.dataset.testid='stop-button';morph.setAttribute('aria-label','Stop generating');morph.click();api.poll();add('morphed','Button changed state');morph.remove();flags.morphedSend=complete()?.id==='morphed';
                  clock+=1000;submit();const cancelled=busy();add('cancelled','Partial');cancelled.click();cancelled.remove();flags.cancelled=complete()===null;
                  clock+=1000;submit();const switching=busy();add('away','Old in other chat');history.pushState({},'', '/c/other');switching.remove();flags.switch=complete()===null;
                  api.enable(false);clock+=1000;submit();const off=busy();add('off','Disabled');off.remove();flags.off=complete()===null;
                  api.enable(true);flags.reenabled=complete()===null;
                  history.pushState({},'', '/');api.poll();clock+=1000;submit();const first=busy();
                  history.pushState({},'', '/c/first');api.poll();
                  const user=document.createElement('div');user.dataset.messageAuthorRole='user';document.body.append(user);
                  history.pushState({},'', '/c/first');add('first','First chat answer');api.poll();first.remove();flags.firstChat=complete()?.id==='first';
                  api.enable(true);clock+=1000;submit();const load=busy();api.enable(true);load.remove();flags.reloadBaseline=complete()===null;
                  return flags;
                }finally{api.enable(false);Date.now=originalNow;}
              })()
              """);
            using var flags=JsonDocument.Parse(raw);
            foreach(var flag in flags.RootElement.EnumerateObject())SmokeAssert(flag.Value.GetBoolean(),"Auto-read: "+flag.Name);
            // Exercise the native claim/persistence path without generating speech in an offline test.
            await core.ExecuteScriptAsync("window.__savedAutoRead=window.__quietAutoRead;window.__quietAutoRead={poll:()=>({path:location.pathname,id:'native-"+Guid.NewGuid()+"',text:'Fixture reply'})};");
            int before=autoReadHandled.Count;
            speechWindow=new Window();reading=new CancellationTokenSource();autoReadEnabled=true;
            try
            {
                await PollAutoRead();
                SmokeAssert(autoReadHandled.Count==before+1,"Native claim missing");
                await PollAutoRead();
                SmokeAssert(autoReadHandled.Count==before+1,"Native duplicate replay");
                using var persisted=JsonDocument.Parse(File.ReadAllText(AutoReadPath));
                SmokeAssert(persisted.RootElement.GetProperty("handled").GetArrayLength()==autoReadHandled.Count,"Handled IDs not persisted");
                await core.ExecuteScriptAsync("history.pushState({},'', '/c/switched');");
                await Task.Delay(100);
                SmokeAssert(reading.IsCancellationRequested,"Chat navigation did not cancel speech");
            }
            finally
            {
                reading?.Dispose();reading=null;speechWindow=null;autoReadEnabled=false;SaveAutoRead();
                await core.ExecuteScriptAsync("window.__quietAutoRead=window.__savedAutoRead;");
            }
            return flags.RootElement.Clone();
        });

        await Check("Maximized window fits monitor work area and restores", async () =>
        {
            WindowState = WindowState.Normal;
            await Task.Delay(150);
            var original = new Rect(Left, Top, Width, Height);
            WindowState = WindowState.Maximized;
            await Task.Delay(250);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var monitor = new BoundsMonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<BoundsMonitorInfo>() };
            SmokeAssert(GetMonitorInfoForBounds(MonitorFromWindowForBounds(hwnd, 2), ref monitor), "Could not read monitor work area.");
            SmokeAssert(GetWindowRect(hwnd, out var actual), "Could not read maximized window bounds.");
            var work = monitor.Work;
            SmokeAssert(Math.Abs(actual.Left - work.Left) <= 1 && Math.Abs(actual.Top - work.Top) <= 1 &&
                Math.Abs(actual.Right - work.Right) <= 1 && Math.Abs(actual.Bottom - work.Bottom) <= 1,
                $"Window ({actual.Left},{actual.Top},{actual.Right},{actual.Bottom}) differs from work area ({work.Left},{work.Top},{work.Right},{work.Bottom}).");
            WindowState = WindowState.Normal;
            await Task.Delay(150);
            SmokeAssert(Math.Abs(Width - original.Width) < 2 && Math.Abs(Height - original.Height) < 2, "Restore changed the normal window size.");
            return new { window = new[] { actual.Left, actual.Top, actual.Right, actual.Bottom }, workArea = new[] { work.Left, work.Top, work.Right, work.Bottom }, restored = true };
        });

        await Check("Maximize respects work area on every connected monitor", async () =>
        {
            var monitors = new List<BoundsMonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr dc, ref BoundsRect bounds, IntPtr data) =>
            {
                var info = new BoundsMonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<BoundsMonitorInfo>() };
                if (GetMonitorInfoForBounds(handle, ref info)) monitors.Add(info);
                return true;
            }, IntPtr.Zero);
            var measured = new List<object>();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            foreach (var info in monitors)
            {
                WindowState = WindowState.Normal;
                var work = info.Work;
                SetWindowPosForBounds(hwnd, IntPtr.Zero, work.Left + 30, work.Top + 30,
                    Math.Min(1000, work.Right - work.Left - 60), Math.Min(760, work.Bottom - work.Top - 60), 0x0014);
                await Task.Delay(150);
                WindowState = WindowState.Maximized;
                await Task.Delay(250);
                SmokeAssert(GetWindowRect(hwnd, out var actual), "Could not inspect maximized bounds.");
                SmokeAssert(actual.Left == work.Left && actual.Top == work.Top && actual.Right == work.Right && actual.Bottom == work.Bottom,
                    $"Window ({actual.Left},{actual.Top},{actual.Right},{actual.Bottom}) differs from work area ({work.Left},{work.Top},{work.Right},{work.Bottom}).");
                measured.Add(new { window = new[] { actual.Left, actual.Top, actual.Right, actual.Bottom }, monitor = new[] { info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom } });
            }
            WindowState = WindowState.Normal;
            return measured;
        });

        await Check("Taskbar offsets on every edge and negative monitor coordinates", () =>
        {
            var monitor = new BoundsRect { Left = -1920, Top = -100, Right = 0, Bottom = 980 };
            var cases = new[] {
                (new BoundsRect { Left = -1920, Top = -100, Right = -48, Bottom = 980 }, 0, 0, 1872, 1080),
                (new BoundsRect { Left = -1872, Top = -100, Right = 0, Bottom = 980 }, 48, 0, 1872, 1080),
                (new BoundsRect { Left = -1920, Top = -52, Right = 0, Bottom = 980 }, 0, 48, 1920, 1032),
                (new BoundsRect { Left = -1920, Top = -100, Right = 0, Bottom = 932 }, 0, 0, 1920, 1032)
            };
            foreach (var (work, x, y, width, height) in cases)
            {
                var limits = new BoundsMinMaxInfo(); ApplyWorkArea(ref limits, monitor, work);
                SmokeAssert(limits.MaxPosition.X == x && limits.MaxPosition.Y == y && limits.MaxSize.X == width && limits.MaxSize.Y == height, "Incorrect taskbar offset.");
            }
            return Task.FromResult<object>(new { edges = 4, secondaryMonitorOrigin = "-1920,-100" });
        });

        await Check("Navigation host and scheme policy", () =>
        {
            string[] allowed = ["https://chatgpt.com/", "https://auth.openai.com/", "https://files.oaiusercontent.com/file", "blob:https://chatgpt.com/id"];
            string[] rejected = ["http://chatgpt.com/", "https://chatgpt.com.evil.test/", "https://evilchatgpt.com/", "https://chatgpt.com@evil.test/", "https://evil.test/?chatgpt.com", "https://chatgpt.com:8443/", "file:///C:/Windows/win.ini", "javascript:alert(1)"];
            foreach (string uri in allowed) SmokeAssert(NavigationPolicy.IsInternal(uri), "Rejected allowed URI: " + uri);
            foreach (string uri in rejected) SmokeAssert(!NavigationPolicy.IsInternal(uri), "Accepted unsafe URI: " + uri);
            return Task.FromResult<object>(new { allowed = allowed.Length, rejected = rejected.Length });
        });

        await Check("Cookie and localStorage survive WebView recreation", async () =>
        {
            string token = Guid.NewGuid().ToString("N");
            async Task<string> OpenProfile(bool write)
            {
                using var view = new WebView2();
                var host = new Window { Content = view, Width = 1, Height = 1, Left = -32000, Top = -32000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
                host.Show();
                try
                {
                    await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(10));
                    view.CoreWebView2.SetVirtualHostNameToFolderMapping("quiet.test", fixtures, CoreWebView2HostResourceAccessKind.DenyCors);
                    await SmokeNavigateAsync(view.CoreWebView2, "https://quiet.test/index.html");
                    if (write)
                        await view.CoreWebView2.ExecuteScriptAsync($"localStorage.setItem('quiet_smoke','{token}');document.cookie='quiet_smoke={token}; Max-Age=86400; Path=/; Secure; SameSite=Lax';");
                    return await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({storage:localStorage.getItem('quiet_smoke'),cookie:document.cookie})");
                }
                finally { host.Content = null; host.Close(); }
            }
            await OpenProfile(true);
            string encoded = await OpenProfile(false);
            string json = JsonSerializer.Deserialize<string>(encoded) ?? "{}";
            using var value = JsonDocument.Parse(json);
            SmokeAssert(value.RootElement.GetProperty("storage").GetString() == token, "Local storage did not persist.");
            SmokeAssert(value.RootElement.GetProperty("cookie").GetString()?.Contains("quiet_smoke=" + token) == true, "Cookie did not persist.");
            return new { profile = environment!.UserDataFolder, scope = "Control recreation using the isolated profile; authenticated login and full process restart are not tested." };
        });

        foreach (bool navigateDownload in new[] { false, true })
        await Check(navigateDownload ? "Download navigation preserves chat and stays quiet" : "Blob download saves exact bytes and stays quiet", async () =>
        {
            await SmokeNavigateAsync(Browser.CoreWebView2, "https://quiet.test/index.html");
            string destination = Path.Combine(dataRoot, "smoke-download-" + Guid.NewGuid().ToString("N") + ".txt");
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CoreWebView2DownloadOperation? operation = null;
            EventHandler<object>? stateChanged = null;
            EventHandler<CoreWebView2DownloadStartingEventArgs> started = (_, e) =>
            {
                if (!e.DownloadOperation.Uri.StartsWith("blob:https://quiet.test/", StringComparison.Ordinal)) return;
                SmokeAssert(e.Handled, "Production download handler did not suppress the automatic flyout.");
                e.ResultFilePath = destination;
                operation = e.DownloadOperation;
                stateChanged = (_, _) =>
                {
                    if (operation.State == CoreWebView2DownloadState.Completed) completed.TrySetResult(true);
                    else if (operation.State == CoreWebView2DownloadState.Interrupted)
                        completed.TrySetException(new IOException("Download interrupted: " + operation.InterruptReason));
                };
                operation.StateChanged += stateChanged;
                stateChanged(null, EventArgs.Empty);
            };
            Browser.CoreWebView2.DownloadStarting += started;
            try
            {
                await Browser.CoreWebView2.ExecuteScriptAsync("window.quietState = 'conversation preserved'; downloadFixture(" + (navigateDownload ? "true" : "false") + ")");
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(500);
                SmokeAssert(await File.ReadAllTextAsync(destination) == "Quiet download verified.\n", "Downloaded content differs.");
                SmokeAssert(StartPanel.Visibility == Visibility.Collapsed && Browser.Visibility == Visibility.Visible, "Download hid the conversation behind an error panel.");
                SmokeAssert(!Browser.CoreWebView2.IsDefaultDownloadDialogOpen, "Download flyout opened automatically.");
                SmokeAssert(await Browser.CoreWebView2.ExecuteScriptAsync("window.quietState") == "\"conversation preserved\"", "Download replaced or reloaded the conversation.");
                SmokeAssert(DownloadBadge.Text == "✓", "Completion badge missing.");
                DownloadsClick(this, new());
                await Task.Delay(500);
                SmokeAssert(downloadPopup?.IsOpen == true, $"Explicit download controls did not open: {downloadCloseReason}; active={IsActive}; closing={closing}; visible={IsVisible}.");
                SmokeAssert(downloadHistory.Any(d => d.Completed && d.FilePath == destination), "Downloaded file missing from the panel.");
                SmokeAssert(File.ReadAllText(DownloadHistoryPath).Contains(Path.GetFileName(destination)), "Completed download was not saved to history.");
                SmokeAssert(DownloadBadge.Visibility == Visibility.Collapsed, "Viewed completion badge was not cleared.");
                CaptureElement((FrameworkElement)downloadPopup!.Child, Path.Combine(output, "downloads.png"));
                downloadPopup.IsOpen = false;
                await Task.Delay(500);
                return new { bytes = new FileInfo(destination).Length, destination };
            }
            finally
            {
                Browser.CoreWebView2.DownloadStarting -= started;
                if (operation is not null && stateChanged is not null) operation.StateChanged -= stateChanged;
            }
        });

        await Check("Target blank reuses the main view", async () =>
        {
            await SmokeNavigateAsync(Browser.CoreWebView2, "https://quiet.test/index.html");
            int requested = 0;
            int windowsBefore = Application.Current.Windows.Count;
            EventHandler<CoreWebView2NewWindowRequestedEventArgs> observer = (_, e) =>
            {
                if (e.Uri == "https://quiet.test/next.html") requested++;
            };
            Browser.CoreWebView2.NewWindowRequested += observer;
            try
            {
                await SmokeNavigationAsync(Browser.CoreWebView2,
                    () => Browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('next').click()"));
                SmokeAssert(requested == 1, "Expected one new-window request.");
                SmokeAssert(Browser.CoreWebView2.Source == "https://quiet.test/next.html", "Link did not navigate the existing view.");
                SmokeAssert(Application.Current.Windows.Count == windowsBefore, "An additional WPF window was created.");
                return new { requested, extraWindows = 0 };
            }
            finally { Browser.CoreWebView2.NewWindowRequested -= observer; }
        });

        await Check("Companion follows submission, streaming, completion, cancellation and pause", async () =>
        {
            await SmokeNavigateAsync(Browser.CoreWebView2, "https://quiet.test/index.html");
            companionReaction.Stop();
            await Task.Delay(400);
            SmokeAssert(pageActivity == "idle", "Composer did not produce idle state.");
            await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-testid=send-button]').click()");
            await Task.Delay(350);
            SmokeAssert(companion!.Activity == "thinking", "Submission did not produce thinking state.");
            await Browser.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend', '<button data-testid=\"stop-button\">Stop</button><div data-message-author-role=\"assistant\"></div>')");
            await Task.Delay(350);
            await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-message-author-role=assistant]').textContent='Local test response'");
            await Task.Delay(350);
            SmokeAssert(companion.Activity == "responding", "Streaming did not produce responding state.");
            await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-testid=stop-button]').remove()");
            await Task.Delay(350);
            SmokeAssert(companion.Activity == "done", "Completion did not produce done state.");
            companionReaction.Stop();
            await Browser.CoreWebView2.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend', '<button data-testid=\"stop-button\">Stop</button>')");
            await Task.Delay(350);
            await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-testid=stop-button]').click();document.querySelector('[data-testid=stop-button]').remove()");
            await Task.Delay(350);
            SmokeAssert(companion.Activity == "idle", "Cancellation was presented as successful completion.");
            await Browser.CoreWebView2.ExecuteScriptAsync("window.__quietCompanionEnabled(false);document.querySelector('[data-testid=send-button]').click()");
            await Task.Delay(350);
            SmokeAssert(companion.Activity == "idle", "Disabled observer still produced activity.");
            await Browser.CoreWebView2.ExecuteScriptAsync("window.__quietCompanionEnabled(true)");
            return new { states = "idle, thinking, responding, done, canceled, paused", conversationTextTransmitted = false };
        });

        await Check("Layered gaze is bounded, changes only eyes, and pauses when hidden", async () =>
        {
            var rig = new LayeredAvatarView { Width = 1254, Height = 1254 };
            rig.Measure(new Size(1254, 1254)); rig.Arrange(new Rect(0, 0, 1254, 1254));
            byte[] Render(double x, double y, string name)
            {
                rig.SetGaze(x, y); rig.UpdateLayout();
                var frame = new RenderTargetBitmap(1254, 1254, 96, 96, PixelFormats.Pbgra32); frame.Render(rig);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
                using (var stream = File.Create(Path.Combine(output, "gaze-" + name + ".png"))) encoder.Save(stream);
                byte[] pixels = new byte[1254 * 1254 * 4]; frame.CopyPixels(pixels, 1254 * 4, 0); return pixels;
            }
            var center = Render(0, 0, "center");
            var left = Render(-1, 0, "left");
            var right = Render(1, 0, "right");
            int changed = 0;
            for (int y = 0; y < 1254; y++) for (int x = 0; x < 1254; x++)
            {
                int i = (y * 1254 + x) * 4;
                bool differs = Enumerable.Range(0, 4).Any(c => left[i+c] != right[i+c]);
                if (!differs) continue;
                changed++;
                SmokeAssert(y >= 379 && y <= 432 && ((x >= 502 && x <= 593) || (x >= 675 && x <= 768)), "Gaze changed pixels outside the eyes.");
            }
            SmokeAssert(changed > 100, "Irises did not visibly move.");
            SmokeAssert(center[(402 * 1254 + 550) * 4 + 3] > 240, "Eye layers did not fill the face opening.");
            rig.SetGaze(20, 20); SmokeAssert(rig.Gaze.Length <= 1.00001, "Extreme gaze exceeded its bounds.");
            rig.AimAtLocalPoint(new Point(634, 480), 1);
            SmokeAssert(rig.Gaze.X > 0 && rig.RightGaze.X < 0, "Eyes did not converge toward the nose.");
            SmokeAssert(rig.Gaze.Y > 0 && rig.RightGaze.Y > 0, "Eyes did not aim down toward the nose.");
            CaptureElement(rig, Path.Combine(output, "gaze-nose.png"));
            rig.AimAtLocalPoint(new Point(10000, 402), 1);
            SmokeAssert(rig.Gaze.X > 0.9 && rig.RightGaze.X > 0.9, "Distant target did not produce parallel gaze.");
            SmokeAssert(rig.Gaze.Length <= 1 && rig.RightGaze.Length <= 1, "Independent eye exceeded travel bounds.");
            companion!.UseEyeTrackingAvatar(); companion.SetCollapsed(false);
            await Task.Delay(100); SmokeAssert(companion.IsEyeTrackingRunning, "Visible gaze timer did not start.");
            companion.SetCollapsed(true); SmokeAssert(!companion.IsEyeTrackingRunning, "Collapsed gaze timer still runs.");
            companion.SetCollapsed(false); companion.Hide(); SmokeAssert(!companion.IsEyeTrackingRunning, "Hidden gaze timer still runs.");
            return new { changedPixels = changed, independentEyeConvergence = true, hiddenTimerStopped = true };
        });

        await Check("Companion sizing, collapse, corners and rendered expressions", async () =>
        {
            companion!.Reveal();
            companion.SetCollapsed(false);
            companion.Width = 230; companion.Height = 284;
            companion.Snap("Bottom right");
            await Task.Delay(250);
            foreach (string pose in new[] { "idle", "thinking", "responding", "done", "error" })
            {
                companion.SetActivity(pose); companion.UpdateLayout();
                CaptureElement(companion, Path.Combine(output, "companion-" + pose + ".png"));
            }
            companion.SetCollapsed(true);
            SmokeAssert(companion.Width == 60 && companion.Collapsed, "Collapse did not make a bubble.");
            companion.SetCollapsed(false);
            SmokeAssert(companion.Width == 230 && !companion.Collapsed, "Expand did not restore the size.");
            companion.Hide();
            return new { width = companion.Width, height = companion.Height, collapsedSize = 60 };
        });

        await Check("Logo ghost fallback is transparent, uses approved art and replaces saved video mode", async () =>
        {
            companion!.UseGhostAvatar();companion.SetCollapsed(false);await Task.Delay(150);
            var ghost=(FrameworkElement)companion.FindName("DefaultAvatar");
            SmokeAssert(ghost.Visibility==Visibility.Visible&&!companion.IsRiggedAvatarRunning&&!companion.IsEyeTrackingRunning,"Ghost mode still runs an avatar timer.");
            var outline=(System.Windows.Shapes.Path)companion.FindName("GhostOutline");
            using var logoStream=new StreamReader(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Quiet.svg")).Stream);
            var source=System.Xml.Linq.XDocument.Parse(logoStream.ReadToEnd());
            var approved=source.Descendants().First(e=>e.Name.LocalName=="path"&&((string?)e.Attribute("d"))?.StartsWith("M 30,189")==true);
            SmokeAssert(outline.Data.ToString()==Geometry.Parse((string)approved.Attribute("d")!).ToString(),"Fallback silhouette differs from the approved logo.");
            CaptureElement(ghost,Path.Combine(output,"logo-ghost.png"));
            var rendered=new RenderTargetBitmap((int)Math.Ceiling(ghost.ActualWidth),(int)Math.Ceiling(ghost.ActualHeight),96,96,PixelFormats.Pbgra32);rendered.Render(ghost);
            byte[] corner=new byte[4];rendered.CopyPixels(new Int32Rect(0,0,1,1),corner,4,0);SmokeAssert(corner[3]==0,"Logo ghost has an opaque background.");
            companion.SetCollapsed(true);SmokeAssert(!companion.IsRiggedAvatarRunning&&!companion.IsEyeTrackingRunning,"Collapsed fallback restarted rendering.");companion.SetCollapsed(false);
            string oldRoot=Path.Combine(output,"retired-video-profile");Directory.CreateDirectory(oldRoot);
            File.WriteAllText(Path.Combine(oldRoot,"companion.json"),"{\"RiggedMode\":false,\"VideoMode\":true,\"LayeredEyes\":true,\"Visible\":false}");
            var old=new CompanionWindow(oldRoot);
            try
            {
                old.Reveal();await Task.Delay(80);
                SmokeAssert(((FrameworkElement)old.FindName("DefaultAvatar")).Visibility==Visibility.Visible&&!old.IsEyeTrackingRunning,"Retired video selection did not migrate to the ghost.");
                SmokeAssert(!File.ReadAllText(Path.Combine(oldRoot,"companion.json")).Contains("VideoMode"),"Retired setting was saved again.");
            }
            finally{old.Close();}
            SmokeAssert(!File.Exists(Path.Combine(AppContext.BaseDirectory,"avatar-video.qav")),"Release still packages the retired video.");
            companion.Hide();
            return new {approvedGhost=true,transparent=true,noFallbackTimer=true,oldModeMigration=true,noPackagedVideo=true};
        });

        await Check("Rigged avatar: activity, native movement, style, and hidden pause", async () =>
        {
            companion!.UseRiggedAvatar(); companion.SetCollapsed(false);
            companion.Width = 360; companion.Height = 440;
            for (int n = 0; n < 100 && !companion.IsRiggedAvatarReady; n++) await Task.Delay(150);
            SmokeAssert(companion.IsRiggedAvatarReady, "Rigged avatar did not start: " + companion.RiggedAvatarError);
            SmokeAssert(companion.IsRiggedAvatarRunning, "Rigged mode not active.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("getComputedStyle(document.querySelector('header')).display==='none' && canvas.getBoundingClientRect().width>100 && document.documentElement.scrollHeight<=innerHeight") == "true", "Desktop portrait layout is not active.");
            companion.SetActivity("thinking"); await Task.Delay(100);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("quietAvatar.getState().state") == "\"thinking\"", "Thinking state missing.");
            companion.SetActivity("responding"); await Task.Delay(100);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("speechPose()") == "-1", "Text streaming incorrectly starts speech.");
            await companion.InspectRiggedAvatarAsync("document.getElementById('clearAccessories').click();document.getElementById('accessory-cap').value='79';document.getElementById('accessory-70').checked=true;saveStyle();true");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("selectedAccessories().length") == "2", "Accessory selection failed.");
            companion.Left += 30; await Task.Delay(180);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("Number.isFinite(hostMotionPrevious?.x)") == "true", "Window movement not delivered.");
            CaptureElement(companion, Path.Combine(output, "rigged-companion.png"));
            companion.SetCollapsed(true); await Task.Delay(200);
            SmokeAssert(!companion.IsRiggedAvatarRunning, "Collapsed rig still running.");
            string frames = await companion.InspectRiggedAvatarAsync("frames"); await Task.Delay(200);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("frames") == frames, "Collapsed renderer keeps drawing.");
            companion.SetCollapsed(false); await Task.Delay(200);
            SmokeAssert(companion.IsRiggedAvatarRunning, "Expanded rig did not resume.");
            companion.Hide(); await Task.Delay(150); SmokeAssert(!companion.IsRiggedAvatarRunning, "Hidden rig still running.");
            SmokeAssert(localSpeech.IsHidden,"Hidden companion did not arm voice unloading");
            return new { localAvatar = true, activityBridge = true, textDoesNotSpeak = true, style = true, windowMotion = true, pause = true };
        });

        await Check("Workshop avatar: approved shapes, fresh layers, physics and depth", async () =>
        {
            companion!.Reveal(true);companion.SetCollapsed(false);await Task.Delay(250);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("!rigManifest.layers.leftNip&&!rigManifest.layers.rightNip&&!document.getElementById('showNipples')&&typeof ChestDeformation.nippleMotion==='function'") == "true", "Public artwork removal changed physics or left a detail layer/control behind.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("quietDesktopRigVersion===2 && Object.keys(v2Rig.hair.nodes).length===12 && rigFaceLibrary.mouths.length===71 && rigFaceLibrary.eyes.length===13") == "true", "The finished workshop rig or expression inventory is missing.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("(()=>{const s=v2Rig.exportSettings();return JSON.stringify(s.rings)===JSON.stringify(quietDesktopPreset.rings)&&JSON.stringify(s.faceDepth)===JSON.stringify(quietDesktopPreset.faceDepth)&&s.preview.strength===1.5&&s.preview.downFlex===1&&s.preview.volumeDepth===1.5&&s.preview.return===1.6&&s.preview.settle===.2&&s.preview.shading===.8&&s.preview.hairAmount===2&&s.preview.hairReturn===.6&&s.preview.hairSettling===.3&&s.preview.hairWind===2;})()") == "true", "Approved workshop settings were not preserved.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("v2Rig.extent===1254 && !document.getElementById('showLowerArtwork').checked && !v2Rig.faceDepth.editing && getComputedStyle(canvas).backgroundColor==='rgba(0, 0, 0, 0)' && v2Rig.renderer.gl.getError()===0 && Object.keys(v2Rig.renderer.layers).length>30") == "true", "Desktop crop, transparency or new GPU layer rendering failed.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("settingsPages.length===6 && document.getElementById('hairAmount').closest('.settingsPage') && document.getElementById('strength').closest('.settingsPage') && document.getElementById('faceEyePose').closest('.settingsPage') ? true : false") == "true", "Live rig settings were not integrated.");
            await companion.InspectRiggedAvatarAsync("controls.autoWind.checked=false;controls.wind.value=0;document.getElementById('rigBounce').click();true");await Task.Delay(80);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("v2Rig.motion.nodes.some(n=>Math.abs(n.y)>1) && v2Rig.motion.nodes.every(n=>Object.values(n).every(Number.isFinite))") == "true", "Accepted chest solver is not active.");
            await companion.InspectRiggedAvatarAsync("v2Rig.motion.reset();document.getElementById('rigHair').click();true");await Task.Delay(80);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("Object.values(v2Rig.hair.nodes).flat().some(n=>Math.abs(n.x)>.05)") == "true", "Separated hair does not respond to motion.");
            await companion.InspectRiggedAvatarAsync("v2Rig.hair.reset();controls.autoWind.checked=true;controls.wind.value=4;true");
            await companion.InspectRiggedAvatarAsync("document.getElementById('hairAmount').value=1.75;document.getElementById('hairAmount').dispatchEvent(new Event('input'));true");
            var rig=(RiggedAvatarView)companion.FindName("RiggedAvatar");
            var reload=new TaskCompletionSource();
            void LoadedAgain(object? sender,CoreWebView2NavigationCompletedEventArgs args)=>reload.TrySetResult();
            rig.CoreWebView2.NavigationCompleted+=LoadedAgain;
            try { rig.CoreWebView2.Reload();await reload.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { rig.CoreWebView2.NavigationCompleted-=LoadedAgain; }
            var restored=false;
            for(var n=0;n<100&&!restored;n++){await Task.Delay(100);restored=await companion.InspectRiggedAvatarAsync("window.quietDesktopRigVersion===2 && v2Rig.exportSettings().preview.hairAmount===1.75 && avatarPreviewState.frames>1")=="true";}
            SmokeAssert(restored,"Native rig edits did not survive renderer recreation.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("JSON.stringify(v2Rig.exportSettings().faceDepth)===JSON.stringify(quietDesktopPreset.faceDepth) && JSON.stringify(ChestDeformation.settings().rings)===JSON.stringify(quietDesktopPreset.rings)")=="true","Reload changed approved shapes or nose settings.");
            await companion.InspectRiggedAvatarAsync("document.getElementById('hairAmount').value=2;document.getElementById('hairAmount').dispatchEvent(new Event('input'));true");
            CaptureElement(companion,Path.Combine(output,"workshop-avatar.png"));
            return new {finishedWorkshop=true,approvedPreset=true,freshFace=true,separatedHair=12,mouthPoses=71,eyePoses=13,transparent=true,livePhysics=true,restartPersistence=true};
        });

        await Check("Native movement retains paired and closely spaced position updates", async () =>
        {
            companion!.Reveal();companion.SetCollapsed(false);await Task.Delay(100);
            var result=await companion.InspectRiggedAvatarAsync("(()=>{const original=v2Rig.windowMove,impact=document.getElementById('windowMotionImpact');const old=impact.value,moves=[];try{impact.value=1;impact.dispatchEvent(new Event('input'));v2Rig.windowMove=(x,y)=>moves.push([x,y]);hostMotionPrevious=null;resetNativeMotion();quietWindowMotion(0,0,0);quietWindowMotion(4,0,1);quietWindowMotion(4,6,2);quietWindowMotion(9,6,3);quietWindowMotion(9,11,4);flushNativeMotion(100);const soft=moves.length===1&&moves[0][0]>0&&moves[0][0]<9;for(let i=1;i<45;i++)flushNativeMotion(100+i*1000/60);const sum=moves.reduce((a,p)=>[a[0]+p[0],a[1]+p[1]],[0,0]);return soft&&Math.abs(sum[0]-9)<1e-8&&Math.abs(sum[1]-11)<1e-8;}finally{v2Rig.windowMove=original;impact.value=old;impact.dispatchEvent(new Event('input'));hostMotionPrevious=null;resetNativeMotion();}})()");
            SmokeAssert(result=="true","Native sampling discarded travel or changed preview-scale motion.");
            return new {pairedAxes=true,sub4msTravel=true,previewScale=true};
        });

        await Check("Particles remain in desktop space during window travel", async () =>
        {
            companion!.Reveal();companion.SetCollapsed(false);
            var result=await companion.InspectRiggedAvatarAsync("""
              (()=>{const saved=particles;try{
                particles=[{x:.55,y:.5}];const r=canvas.getBoundingClientRect(),x=particles[0].x*r.width,y=particles[0].y*r.height;
                shiftParticleField(15,-12);
                const fixed=Math.abs(particles[0].x*r.width+15-x)<1e-8&&Math.abs(particles[0].y*r.height-12-y)<1e-8;
                shiftParticleField(2000,-3000);
                return fixed&&particles.every(p=>p.x>=0&&p.x<1&&p.y>=0&&p.y<1)&&particleAnchorState.space==='desktop';
              }finally{particles=saved;}})()
              """);
            SmokeAssert(result=="true","Window travel pulled particles with the avatar.");
            return new {desktopCoordinates=true,windIndependent=true,wrappedEdges=true};
        });

        await Check("Petal attention uses real blossoms, yields to the mouse and respects opt-out", async () =>
        {
            companion!.Reveal(true);companion.SetCollapsed(false);await Task.Delay(100);
            var gazeField=typeof(CompanionWindow).GetField("gazeTimer",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var gazeTimer=(System.Windows.Threading.DispatcherTimer)gazeField.GetValue(companion)!;
            bool wasTracking=gazeTimer.IsEnabled;gazeTimer.Stop();
            try
            {
                var prepared=await companion.InspectRiggedAvatarAsync("""
                  (()=>{
                    window.petalFixture={particles,settings:{...particleSettings},wind:atmosphere.wind,attention:{...petalAttention,position:[...petalAttention.position]},mouse:petalMouseAt,motion:petalMotionAt,cursor:petalCursor,window:petalWindow,storage:localStorage.getItem('quiet-particles-v1'),elements:Object.fromEntries(['eyes','personality','subtleLife','expression','faceEyePose'].map(k=>{const e=document.getElementById(k);return[k,e.type==='checkbox'?e.checked:e.value];})),moodUntil:chatActivity.moodUntil,index:expressionIndex,opacity:expressionOpacity,curious:curiousMix,rub:rubComfort,idle:{...naturalIdle},idleExpression,gazeMode,lastPointerTime,randomLook,smoothLook:[...smoothLook],headLook:[...headLook]};
                    controls.eyes.checked=controls.personality.checked=true;document.getElementById('subtleLife').checked=true;controls.expression.value='auto';document.getElementById('faceEyePose').value='auto';expressionIndex=-1;expressionOpacity=0;curiousMix=rubComfort=0;chatActivity.moodUntil=-1;naturalIdle.mode=null;naturalIdle.nextAt=time+100;idleExpression=-1;gazeMode="idle";lastPointerTime=time-20;randomLook=[633,405];smoothLook=[633,405];headLook=[633,405];
                    particles=Array.from({length:10},(_,i)=>({...makeParticle(i),x:i===9?.72:.9,y:i===9?.28:.9,rear:i!==9}));
                    Object.assign(particleSettings,{type:'blossoms',count:10,watchPetals:true});atmosphere.wind=true;
                    petalMouseAt=petalMotionAt=time-20;Object.assign(petalAttention,{active:null,mix:0,blocked:false,nextAt:time});stepPetalAttention(.05);
                    return petalAttention.active===particles[9]&&petalAttention.until-time>=1.8&&petalAttention.until-time<=3.2;
                  })()
                  """);
                SmokeAssert(prepared=="true","Idle glance did not target a real visible front petal.");
                await Task.Delay(250);
                var petalRendered=await companion.InspectRiggedAvatarAsync("JSON.stringify({attention:petalAttentionState,eyes:avatarPreviewState.gaze,expression:expressionIndex,opacity:expressionOpacity,mode:gazeMode,effective:effectiveGaze(),gpu:v2Rig.renderer.gl.getError(),head:headLook,smooth:smoothLook})");
                await File.WriteAllTextAsync(Path.Combine(output,"petal-attention-state.json"),JsonSerializer.Deserialize<string>(petalRendered));
                CaptureElement(companion,Path.Combine(output,"petal-glance.png"));
                SmokeAssert(await companion.InspectRiggedAvatarAsync("petalAttentionState.active&&petalAttentionState.amount>.3&&avatarPreviewState.gaze[0][0]>.2&&v2Rig.renderer.gl.getError()===0")=="true","Petal gaze did not reach the rendered irises: "+petalRendered);
                var rig=(RiggedAvatarView)companion.FindName("RiggedAvatar");
                var previousCursor=await companion.InspectRiggedAvatarAsync("JSON.stringify(lastHostCursor)");
                rig.Aim(37,25,false);await Task.Delay(100);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("!petalAttentionState.active&&petalAttentionState.amount===0&&petalAttentionState.mouseIdle<1")=="true","Mouse movement did not immediately interrupt the glance.");
                SmokeAssert(await companion.InspectRiggedAvatarAsync("JSON.stringify(lastHostCursor)")==previousCursor,"Activity-only cursor message enabled mouse gaze tracking.");
                var guards=await companion.InspectRiggedAvatarAsync("""
                  (()=>{
                    const ready=()=>{petalMouseAt=petalMotionAt=time-20;Object.assign(petalAttention,{active:null,mix:0,blocked:false,nextAt:time});};
                    for(const type of ['leaves','dust','rain','mist']){particleSettings.type=type;ready();stepPetalAttention(.05);if(petalAttentionState.active)return false;}
                    particleSettings.type='blossoms';ready();stepPetalAttention(.05);if(!petalAttentionState.active)return false;
                    petalAttention.until=time-.01;stepPetalAttention(.05);if(petalAttentionState.active||petalAttentionState.amount<=0||petalAttentionState.nextAt-time<45)return false;
                    const control=document.getElementById('watchPetals');control.checked=false;control.dispatchEvent(new Event('input'));ready();stepPetalAttention(.05);
                    return !petalAttentionState.active&&JSON.parse(localStorage.getItem('quiet-particles-v1')).watchPetals===false;
                  })()
                  """);
                SmokeAssert(guards=="true","Type restriction, brief duration, smooth exit or saved petal opt-out failed.");
            }
            finally
            {
                await companion.InspectRiggedAvatarAsync("""
                  (()=>{const s=window.petalFixture;if(!s)return;particles=s.particles;Object.assign(particleSettings,s.settings);atmosphere.wind=s.wind;Object.assign(petalAttention,s.attention);petalMouseAt=s.mouse;petalMotionAt=s.motion;petalCursor=s.cursor;petalWindow=s.window;chatActivity.moodUntil=s.moodUntil;expressionIndex=s.index;expressionOpacity=s.opacity;curiousMix=s.curious;rubComfort=s.rub;Object.assign(naturalIdle,s.idle);idleExpression=s.idleExpression;gazeMode=s.gazeMode;lastPointerTime=s.lastPointerTime;randomLook=s.randomLook;smoothLook=s.smoothLook;headLook=s.headLook;
                  for(const [k,v]of Object.entries(s.elements)){const e=document.getElementById(k);if(e.type==='checkbox')e.checked=v;else e.value=v;}if(s.storage===null)localStorage.removeItem('quiet-particles-v1');else localStorage.setItem('quiet-particles-v1',s.storage);syncParticles();delete window.petalFixture;})()
                  """);
                if(wasTracking)gazeTimer.Start();
            }
            return new {actualPetal=true,renderedEyes=true,mouseInterrupt=true,trackingOptOutPreserved=true,blossomsOnly=true,brief=true,longCooldown=true,smoothExit=true,savedOptOut=true};
        });

        await Check("Reply collection supports semantic turns, missing IDs and hidden stop controls", async () =>
        {
            var core=Browser.CoreWebView2;
            await SmokeNavigateAsync(core,"https://quiet.test/index.html");
            await core.ExecuteScriptAsync(ReplyReaderScript);
            var raw=await core.ExecuteScriptAsync("""
              (()=>{
                const article=document.createElement('article');article.dataset.turn='assistant';article.innerHTML='<div class="markdown">A completed reply.<button>Copy</button><pre>Skip this code.</pre></div><button data-testid="copy-turn-action-button">Copy</button>';document.body.append(article);
                const hidden=document.createElement('button');hidden.dataset.testid='stop-button';hidden.hidden=true;document.body.append(hidden);
                const api=window.__quietReplyReader,first=api.latest(),id=api.id(article);
                const flags={semantic:first.state==='ready'&&first.text==='A completed reply.',identity:!!id&&api.id(article)===id,hiddenStop:first.state==='ready'};
                hidden.hidden=false;flags.streaming=api.latest().state==='streaming';hidden.remove();article.remove();
                const fallback=document.createElement('article');fallback.dataset.testid='conversation-turn-2';fallback.innerHTML='<div class="prose">A newer completed layout.</div><button data-testid="copy-turn-action-button">Copy</button>';document.body.append(fallback);
                const user=document.createElement('article');user.dataset.turn='user';user.innerHTML='<div class="markdown">Never read my prompt.</div><button data-testid="copy-turn-action-button">Copy</button>';document.body.append(user);
                flags.newerLayout=api.latest().text==='A newer completed layout.';flags.excludeUser=api.replies().length===1;
                return flags;
              })()
              """);
            using var flags=JsonDocument.Parse(raw);
            foreach(var flag in flags.RootElement.EnumerateObject())SmokeAssert(flag.Value.GetBoolean(),"Reply reader: "+flag.Name);
            return flags.RootElement.Clone();
        });

        await Check("Live modern ChatGPT layout: assistant text, real IDs, composer and completion", async () =>
        {
            var core=Browser.CoreWebView2;await SmokeNavigateAsync(core,"https://quiet.test/index.html");
            await core.ExecuteScriptAsync(ReplyReaderScript);await core.ExecuteScriptAsync(AutoReadScript);
            var raw=await core.ExecuteScriptAsync("""
                (()=>{
                    document.querySelector('form').remove();
                    const compose=document.createElement('div');compose.setAttribute('data-composer-layout','');compose.innerHTML='<div data-composer-markdown contenteditable="true" role="textbox"></div><button aria-label="Send">Send</button><button aria-label="Stop" hidden>Stop</button>';document.body.append(compose);
                    const add=(id,text)=>{const turn=document.createElement('div');turn.setAttribute('data-turn-key',id);turn.innerHTML='<div data-content-search-unit-key="'+id+':2:assistant"><div data-chatgpt-selection-message-id="'+id+'"><div data-markdown-text-style="assistant-message"><p>'+text+'</p><p>Second paragraph.</p></div></div></div><button aria-label="Rate response">Rate</button>';document.body.append(turn);return turn;};
                    add('modern-first','Modern completed reply.');
                    const user=document.createElement('div');user.setAttribute('data-content-search-unit-key','user:1:user');user.innerHTML='<div data-chatgpt-selection-message-id="user"><div data-markdown-text-style="assistant-message"><p>Do not read the prompt, even if it reuses the text style.</p></div></div>';document.body.append(user);
                    const api=window.__quietReplyReader,node=api.replies().at(-1),first=api.latest();
                    const flags={modernText:first.text==='Modern completed reply.\nSecond paragraph.',excludeUser:api.replies().length===1,realId:api.id(node)==='modern-first',completed:api.completed(node),hiddenStop:first.state==='ready'};
                    const clock=Date.now;let now=clock();Date.now=()=>now;
                    try{
                        const auto=window.__quietAutoRead;auto.enable(true);flags.history=auto.poll()===null;
                        compose.firstChild.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));add('modern-enter','Enter reply.');auto.poll();now+=2200;flags.enter=auto.poll()?.id==='modern-enter';
                        auto.enable(true);now+=1000;compose.querySelector('[aria-label="Send"]').dispatchEvent(new PointerEvent('pointerdown',{button:0,bubbles:true}));add('modern-button','Send-button reply.');auto.poll();now+=2200;flags.send=auto.poll()?.id==='modern-button';
                        auto.enable(true);compose.firstChild.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));add('modern-stream','Still writing.');const stop=compose.querySelector('[aria-label="Stop"]');stop.hidden=false;flags.streaming=api.latest().state==='streaming'&&auto.poll()===null;stop.hidden=true;auto.poll();now+=2200;flags.finished=auto.poll()?.id==='modern-stream';
                        auto.enable(true);now+=1000;compose.firstChild.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true}));add('modern-cancel','Cancelled.');stop.hidden=false;stop.click();stop.hidden=true;auto.poll();now+=2200;flags.cancelled=auto.poll()===null;
                    }finally{Date.now=clock;window.__quietAutoRead.enable(false);}
                    return flags;
                })()
                """);
            using var flags=JsonDocument.Parse(raw);foreach(var flag in flags.RootElement.EnumerateObject())SmokeAssert(flag.Value.GetBoolean(),"Modern layout: "+flag.Name);return flags.RootElement.Clone();
        });

        await Check("Smiley speech: sentence association, literal protection and restrained tags", () =>
        {
            var plan=SpeechSegments("It worked! 😊 Next task. Sad 😔. Surprise :o.");
            SmokeAssert(plan.Count==4&&plan[0].Text=="It worked!"&&plan[0].Mood=="happy"&&plan[1].Mood==null&&plan[2].Mood=="sad"&&plan[3].Mood=="surprised","Sentence smiley association is wrong.");
            SmokeAssert(SpeechSegments(":) Good morning.")[0].Mood=="happy","Leading smiley missing.");
            var affectionate=SpeechSegments("Meccha daisuki da yo too, Anata. 💛\nCome here, you sentimental menace. 😏💋");
            SmokeAssert(affectionate.Count==2&&affectionate[0].Mood=="affectionate"&&affectionate[1].Mood=="kiss+playful"&&affectionate[0].Text=="Meccha daisuki da yo too, Anata."&&affectionate[1].Text=="Come here, you sentimental menace.","Ray's emoji combination or sentence placement is wrong.");
            SmokeAssert(SpeechSegments("Thanks ❤️. Hi ;). Kiss 💋😉.").Select(p=>p.Mood).SequenceEqual(new[]{"affectionate","wink","wink"}),"Heart variants or wink cues missing.");
            SmokeAssert(SpeechSegments("Warm 😉💛.")[0].Mood=="affectionate+wink","Wink was lost beside a heart.");

            SmokeAssert(SpeechSegments("Great 👍🌱.")[0].Text=="Great.","Unknown emoji names reached speech.");
            SmokeAssert(SpeechSegments("Use `:)` and `value` and https://example.org/:). ").All(s=>s.Mood==null)&&SpeechSegments("```😂``` Normal.")[0].Mood==null,"Code or URLs supplied a cue.");
            var off=SpeechSegments("Happy 😊. Funny 😂.",false,true);SmokeAssert(off.All(s=>s.Mood==null&&!s.Text.Contains("[chuckle]")),"Disabled smiley cues still active.");
            var laugh=SpeechSegments("Funny 😂. Again 🤣.",true,true);SmokeAssert(laugh.Count(s=>s.Text.Contains("[chuckle]"))==1,"Too many automatic chuckles.");
            var explicitTag=SpeechSegments("[laugh] Funny 😂. Again 🤣.",true,true);SmokeAssert(explicitTag.All(s=>!s.Text.Contains("[chuckle]")),"Automatic sound duplicated an explicit tag.");
            SmokeAssert(SpeechSegments("Normal words. 🙂")[0].Text=="Normal words.","Trailing smiley was spoken.");
            SmokeAssert(SpeechSegments("Try `"+new string('a',800)+"` 😊.").All(s=>s.Text.Length<=300),"Restored inline text exceeded speech chunk bound.");
            return Task.FromResult<object>(new {sentences=true,leading=true,emojiOmitted=true,codeAndUrls=true,optOut=true,oneChuckle=true,explicitTags=true,bounded=true});
        });

        await Check("Reading controls retain audio position, speed and cancel while paused", async () =>
        {
            companion!.Reveal();companion.SetCollapsed(false);
            using var sample=JsonDocument.Parse(await companion.InspectRiggedAvatarAsync("builtInVoices[1]"));
            companion.SetSpeechRate(1);companion.SetSpeechPaused(false);
            var play=companion.PlaySpeech(sample.RootElement,CancellationToken.None,"happy");
            await Task.Delay(700);companion.SetSpeechPaused(true);await Task.Delay(100);
            double before=double.Parse(await companion.InspectRiggedAvatarAsync("voice.currentTime"),System.Globalization.CultureInfo.InvariantCulture);
            await Task.Delay(550);
            double after=double.Parse(await companion.InspectRiggedAvatarAsync("voice.currentTime"),System.Globalization.CultureInfo.InvariantCulture);
            SmokeAssert(Math.Abs(after-before)<.01&&!play.IsCompleted,"Pause cancelled the clip or kept audio running.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("speechUserPaused&&!!hostSpeechId&&voice.paused&&v2Rig.speechTargetWeights===null&&naturalIdleState.blocked")=="true","Paused audio retained speaking or idle-expression targets.");
            companion.SetSpeechRate(.75);companion.SetSpeechPaused(false);await Task.Delay(350);
            SmokeAssert(await companion.InspectRiggedAvatarAsync($"voiceActive()&&voice.playbackRate===.75&&voice.preservesPitch&&voice.currentTime>{after.ToString(System.Globalization.CultureInfo.InvariantCulture)}&&speechTimingState.active&&v2Rig.speechBlend.weights.every((v,i)=>Math.abs(v-speechTimingState.weights[i])<.001)")=="true","Resume lost position, rate, pitch or timed mouth movement.");
            companion.SetSpeechPaused(true);await Task.Delay(100);companion.StopSpeech();
            try{await play;throw new InvalidOperationException("Stop did not cancel paused speech.");}catch(OperationCanceledException){}
            await Task.Delay(100);SmokeAssert(await companion.InspectRiggedAvatarAsync("voice.paused&&!hostSpeechId&&!speechUserPaused")=="true","Stop left a paused clip attached.");
            companion.SetSpeechPaused(true);play=companion.PlaySpeech(sample.RootElement,CancellationToken.None);await Task.Delay(250);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("voice.paused&&voice.currentTime===0&&speechUserPaused&&!!hostSpeechId")=="true"&&!play.IsCompleted,"A clip started playing while the read was paused.");
            companion.HideCompanion();try{await play;throw new InvalidOperationException("Hide retained paused speech.");}catch(OperationCanceledException){}
            companion.SetSpeechPaused(false);companion.SetSpeechRate(1);companion.Reveal();companion.SetCollapsed(false);
            using(var cts=new CancellationTokenSource()){
                reading=cts;SetReadingPaused(true);var gate=WaitForReadingResume(cts.Token);
                SmokeAssert(!gate.IsCompleted,"Pause between sentences did not gate playback.");SetReadingPaused(false);await gate;
                SetReadingPaused(true);gate=WaitForReadingResume(cts.Token);StopReading();
                try{await gate;throw new InvalidOperationException("Stop did not cancel the sentence gate.");}catch(OperationCanceledException){}
                reading=null;UpdateReadingControls();
            }
            ReadReplyClick(this,new());for(int i=0;i<30&&speechWindow==null;i++)await Task.Delay(100);
            SmokeAssert(speechWindow!=null&&speechPauseButton?.IsEnabled==false,"Reading controls did not open in their idle state.");
            CaptureElement(speechWindow!,Path.Combine(output,"reading-controls.png"));speechWindow!.Close();
            return new {pauseResume=true,positionRetained=true,speedAndPitch=true,visemeClock=true,startPaused=true,hideCancels=true,sentenceGate=true};
        });

        await Check("Speech timing: short lip contact, shared jaw, seeking and silent gaps", async () =>
        {
            companion!.Reveal(true);companion.SetCollapsed(false);
            string inspected=await companion.InspectRiggedAvatarAsync("""
              (()=>{
                const saved={active:voiceActive,cues:voiceCues,plan:nativeSpeechPlan,weights:v2Rig.speechTargetWeights,pose:audioPose,position:voice.currentTime};
                const frames={};
                try{
                  voice.pause();voiceCues=[{start:.1,end:.25,pose:4},{start:.25,end:.28,pose:1},{start:.28,end:.55,pose:2},{start:.65,end:.8,pose:6}];
                  nativeSpeechPlan=SpeechMotion.prepare(voiceCues,1);voiceActive=()=>true;
                  function sample(t,dt){
                    voice.currentTime=t;stepAudio();v2Rig.advance(dt,false,0,0,0,audioPose,0,-1,0);
                    const c=document.createElement('canvas');c.width=c.height=1254;const g=c.getContext('2d');v2Rig.faceBase(g);v2Rig.mouth(g);v2Rig.drawEyes(g,[[0,0],[0,0]],0,0);
                    return {weights:[...v2Rig.speechBlend.weights],jaw:v2Rig.speechBlend.jaw,image:c.toDataURL('image/png')};
                  }
                  frames.open=sample(.15,1/60);frames.closed=sample(.24,1/60);frames.vowel=sample(.33,1/60);frames.silent=sample(.58,1/60);frames.seek=sample(.24,.05);
                  const flags={openJaw:Math.abs(frames.open.jaw-18)<.001,closedContact:frames.closed.weights[1]>.99&&frames.closed.jaw<.001,vowelJaw:Math.abs(frames.vowel.jaw-11)<.001,silentRest:frames.silent.weights[0]>.99&&frames.silent.jaw<.001,seekAndSlowFrame:frames.seek.weights.every((v,i)=>Math.abs(v-frames.closed.weights[i])<.001),gpu:v2Rig.renderer.gl.getError()===0};
                  return JSON.stringify({flags,images:{open:frames.open.image,closed:frames.closed.image}});
                }finally{voiceActive=saved.active;voiceCues=saved.cues;nativeSpeechPlan=saved.plan;v2Rig.speechTargetWeights=saved.weights;audioPose=saved.pose;voice.currentTime=saved.position;}
              })()
              """);
            using var rendered=JsonDocument.Parse(JsonSerializer.Deserialize<string>(inspected)!);
            foreach(var flag in rendered.RootElement.GetProperty("flags").EnumerateObject())SmokeAssert(flag.Value.GetBoolean(),"Speech timing failed: "+flag.Name);
            foreach(var picture in rendered.RootElement.GetProperty("images").EnumerateObject())File.WriteAllBytes(Path.Combine(output,"speech-polish-"+picture.Name+".png"),Convert.FromBase64String(picture.Value.GetString()!.Split(',')[1]));
            using var sample=JsonDocument.Parse(await companion.InspectRiggedAvatarAsync("builtInVoices[1]"));
            var playback=companion.PlaySpeech(sample.RootElement,CancellationToken.None);await Task.Delay(750);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("window.speechTimingState.active&&v2Rig.speechBlend.weights.every((v,i)=>Math.abs(v-window.speechTimingState.weights[i])<.001)")=="true","Actual playback did not use the media-clock weights.");
            companion.StopSpeech();try{await playback;}catch(OperationCanceledException){}
            await Task.Delay(150);SmokeAssert(await companion.InspectRiggedAvatarAsync("v2Rig.speechTargetWeights===null")=="true","Stopped audio retained a speech target.");
            return new {shortClosedContact=true,jawSynchronized=true,silentGaps=true,seekAndSlowFrame=true,actualPlayback=true,cancellation=true,gpu=true};
        });

        await Check("Speech smileys: smooth authored eyes, timed lips and cancellation", async () =>
        {
            companion!.Reveal(true);companion.SetCollapsed(false);await Task.Delay(100);
            var original=await companion.InspectRiggedAvatarAsync("JSON.stringify({eye:document.getElementById('faceEyePose').value,expression:controls.expression.value})");
            using var choices=JsonDocument.Parse(JsonSerializer.Deserialize<string>(original)!);
            await companion.InspectRiggedAvatarAsync("document.getElementById('faceEyePose').value='auto';controls.expression.value='auto';paused=false;hostRunning=true;true");
            using var sample=JsonDocument.Parse(await companion.InspectRiggedAvatarAsync("builtInVoices[1]"));
            try
            {
                using(var cancel=new CancellationTokenSource())
                {
                    var play=companion.PlaySpeech(sample.RootElement,cancel.Token,"happy");await Task.Delay(900);
                    var live=await companion.InspectRiggedAvatarAsync("(()=>{const s=window.speechEmotionState;return !!s&&s.active&&s.amount>.8&&v2Rig.eyeState.key==='library-eyes0'&&v2Rig.browTarget.lift>0&&speechPose()===audioPose&&document.getElementById('faceEyePose').value==='auto';})()");
                    SmokeAssert(live=="true","Speech emotion did not drive eyes/brows alongside timed lips.");
                    await companion.InspectRiggedAvatarAsync("document.getElementById('faceEyePose').value='eyes1';true");await Task.Delay(650);
                    SmokeAssert(await companion.InspectRiggedAvatarAsync("v2Rig.eyeState.key==='library-eyes1'&&document.getElementById('faceEyePose').value==='eyes1'")=="true","Speech changed manual eye selection.");
                    cancel.Cancel();companion.StopSpeech();try{await play;}catch(OperationCanceledException){}
                    await Task.Delay(150);SmokeAssert(await companion.InspectRiggedAvatarAsync("speechEmotion.mood===null&&speechEmotion.amount===0")=="true","Cancelled speech left a mood behind.");
                }
                await companion.InspectRiggedAvatarAsync("document.getElementById('faceEyePose').value='auto';true");
                await companion.PlaySpeech(sample.RootElement,CancellationToken.None,"sad");await Task.Delay(120);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("speechEmotion.mood==='sad'&&speechEmotion.afterUntil>time&&selectedExpression()===1063")=="true","Completed clip lost its short authored-mouth postlude.");
                await Task.Delay(2600);SmokeAssert(await companion.InspectRiggedAvatarAsync("speechEmotion.afterUntil<time&&speechEmotion.amount<.03")=="true","Speech mood did not ease out.");
                await companion.InspectRiggedAvatarAsync("speechEmotion.mood='happy';speechEmotion.amount=1;true");companion.HideCompanion();await Task.Delay(100);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("speechEmotion.mood===null&&speechEmotion.amount===0")=="true","Hidden companion retained speech mood.");
            }
            finally
            {
                companion.StopSpeech();companion.Reveal(true);companion.SetCollapsed(false);
                await companion.InspectRiggedAvatarAsync("document.getElementById('faceEyePose').value="+JsonSerializer.Serialize(choices.RootElement.GetProperty("eye").GetString())+";controls.expression.value="+JsonSerializer.Serialize(choices.RootElement.GetProperty("expression").GetString())+";true");
            }
            return new {eyesAndBrows=true,timedLips=true,manualPosePreserved=true,cancellation=true,postlude=true,settling=true,hiddenReset=true};
        });

        await Check("Affectionate speech: combined eyes, longer mouth postlude and smooth wink", async () =>
        {
            companion!.Reveal(true);companion.SetCollapsed(false);
            await companion.InspectRiggedAvatarAsync("document.getElementById('faceEyePose').value='auto';controls.expression.value='auto';hostRunning=true;paused=false;true");
            using var sample=JsonDocument.Parse(await companion.InspectRiggedAvatarAsync("builtInVoices[1]"));
            try
            {
                var play=companion.PlaySpeech(sample.RootElement,CancellationToken.None,"kiss+playful");await Task.Delay(1000);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("v2Rig.eyeState.key==='library-eyes12'&&speechPose()===audioPose&&window.naturalIdleState.blocked")=="true","Smirk/kiss combination displaced speech or idle competed with it.");
                await companion.InspectRiggedAvatarAsync("voice.currentTime=voice.duration-.08;true");await play;
                await Task.Delay(100);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("speechEmotion.afterUntil-time>1.1&&selectedExpression()===rigFaceLibrary.mouths.find(l=>l.name==='Pout 01').index")=="true","Kiss postlude did not linger with the existing mouth pose.");
                companion.StopSpeech();await Task.Delay(100);
                var winkPlay=companion.PlaySpeech(sample.RootElement,CancellationToken.None,"wink");await Task.Delay(850);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("speechEmotion.winkAt>=0&&speechPose()===audioPose&&v2Rig.renderer.gl.getError()===0")=="true","Wink cue failed or interrupted speech rendering.");
                companion.StopSpeech();try{await winkPlay;}catch(OperationCanceledException){}
                // StopSpeech releases the native waiter before WebView processes
                // the posted stop. Inspect only after its asynchronous cleanup.
                bool winkCleared=false;
                for(int i=0;i<20&&!winkCleared;i++){await Task.Delay(50);winkCleared=await companion.InspectRiggedAvatarAsync("speechEmotion.winkAt===-1&&speechEmotion.amount===0")=="true";}
                SmokeAssert(winkCleared,"Stopped wink remained active.");
            }
            finally{companion.StopSpeech();}
            return new {combinedCues=true,timedLips=true,lingeringKiss=true,wink=true,idleYields=true};
        });

        await Check("Actual lip, Read aloud and auto-read paths collect and play completed replies", async () =>
        {
            var core=Browser.CoreWebView2;
            await SmokeNavigateAsync(core,"https://quiet.test/index.html");
            await core.ExecuteScriptAsync(ReplyReaderScript);
            await core.ExecuteScriptAsync(AutoReadScript);
            await core.ExecuteScriptAsync("const replyFixture=document.createElement('div');replyFixture.setAttribute('data-turn-key','modern-lip');replyFixture.innerHTML='<div data-chatgpt-selection-message-id=modern-lip><div data-markdown-text-style=assistant-message>Fixture reply for lip reading. 😊</div></div><button aria-label=\"Rate response\">Rate</button>';document.body.append(replyFixture);true");
            using var sample=JsonDocument.Parse(await companion!.InspectRiggedAvatarAsync("builtInVoices[1]"));
            string bundleFile=Path.Combine(fixtures,"speech-fixture.json"),requestFile=Path.Combine(fixtures,"speech-request.json"),workerFile=Path.Combine(fixtures,"speech-fixture-worker.py");
            await File.WriteAllTextAsync(bundleFile,sample.RootElement.GetRawText());
            await File.WriteAllTextAsync(workerFile,"import sys,json\nfrom pathlib import Path\nroot=Path(__file__).parent\nfor line in sys.stdin:\n r=json.loads(line);(root/'speech-request.json').write_text(json.dumps(r),encoding='utf-8');b=json.loads((root/'speech-fixture.json').read_text(encoding='utf-8'));b['transcript']=r['text'];print(json.dumps(dict(ok=True,engine=r['engine'],fallback=False,bundle=b)),flush=True)\n");
            var testSpeech=new LocalSpeech(_=>{var start=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("QUIET_TEST_PYTHON") ?? PortablePaths.VoicePython){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("-u");start.ArgumentList.Add(workerFile);return start;});
            var originalSpeech=localSpeech;
            var speechField=typeof(MainWindow).GetField(nameof(localSpeech),System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            bool oldAuto=autoReadEnabled;autoReadTimer.Stop();
            speechField.SetValue(this,testSpeech);
            async Task WaitForSpeech()
            {
                for(int n=0;n<80;n++){await Task.Delay(50);if(await companion.InspectRiggedAvatarAsync("voiceActive()")=="true")return;}
                throw new InvalidOperationException("Speech did not start: "+StatusText.Text);
            }
            async Task WaitForStop(){StopReading();for(int n=0;n<60&&reading!=null;n++)await Task.Delay(50);SmokeAssert(reading==null,"Speech request did not release after stop.");}
            try
            {
                companion.Reveal(true);companion.SetCollapsed(false);
                await companion.InspectRiggedAvatarAsync("controls.reactions.checked=false;hostRunning=true;paused=false;lastLipRead=-10000;const lip=canvas.getBoundingClientRect();canvas.dispatchEvent(new PointerEvent('pointerdown',{clientX:lip.left+lip.width*630/1254,clientY:lip.top+lip.width*558/1254,bubbles:true,button:0}));true");
                await WaitForSpeech();
                SmokeAssert(await companion.InspectRiggedAvatarAsync("speechEmotion.mood==='happy'")=="true","Lip trigger did not carry the smiley mood.");
                using(var request=JsonDocument.Parse(await File.ReadAllTextAsync(requestFile)))SmokeAssert(request.RootElement.GetProperty("text").GetString()=="Fixture reply for lip reading.","Lip reading selected the wrong reply.");
                await WaitForStop();
                ReadReplyClick(this,new());for(int n=0;n<40&&speechWindow==null;n++)await Task.Delay(50);
                var panel=(FrameworkElement)speechWindow!.Content;
                var editor=SmokeControls<System.Windows.Controls.TextBox>(panel).Single();SmokeAssert(editor.Text=="Fixture reply for lip reading. 😊","Read aloud did not populate the completed reply.");
                SmokeControls<System.Windows.Controls.Button>(panel).Single(b=>b.Content.ToString()=="Read").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await WaitForSpeech();await WaitForStop();speechWindow.Close();await Task.Delay(100);
                autoReadEnabled=true;
                await core.ExecuteScriptAsync("window.__quietAutoRead.enable(true);document.querySelector('[data-testid=send-button]').click();const next=document.createElement('div');next.setAttribute('data-turn-key','modern-auto');next.innerHTML='<div data-markdown-text-style=assistant-message>New reply without a message ID. 😔</div><button aria-label=\"Rate response\">Rate</button>';document.body.append(next);window.__quietAutoRead.poll();true");
                await Task.Delay(2250);
                var polling=PollAutoRead();await WaitForSpeech();
                using(var request=JsonDocument.Parse(await File.ReadAllTextAsync(requestFile)))SmokeAssert(request.RootElement.GetProperty("text").GetString()=="New reply without a message ID.","Automatic reading selected the wrong reply.");
                await WaitForStop();await polling.WaitAsync(TimeSpan.FromSeconds(3));
                return new {lipCollected=true,lipIndependentOfReactions=true,readPanelPopulated=true,manualPlayback=true,autoReadWithoutIds=true};
            }
            finally
            {
                StopReading();speechWindow?.Close();autoReadEnabled=oldAuto;autoReadTimer.Start();
                speechField.SetValue(this,originalSpeech);testSpeech.Dispose();
                await companion.InspectRiggedAvatarAsync("controls.reactions.checked=true;true");
                await core.ExecuteScriptAsync("window.__quietAutoRead.enable(false);true");
            }
        });

        await Check("Toolbar toggles, visible auto-read option, and lip read gesture", async () =>
        {
            companion!.Reveal();CompanionClick(this,new());SmokeAssert(!companion.IsVisible,"Companion did not hide");
            CompanionClick(this,new());SmokeAssert(companion.IsVisible,"Companion did not reopen");
            ReadReplyClick(this,new());for(int i=0;i<30&&speechWindow==null;i++)await Task.Delay(50);
            SmokeAssert(speechWindow!=null,"Voice panel missing");
            var panel=(FrameworkElement)speechWindow!.Content;
            var voiceChoices=SmokeControls<System.Windows.Controls.ComboBox>(panel).Single(c=>c.Tag?.ToString()=="speech-voice");
            SmokeAssert(voiceChoices.Items.Count==1&&voiceChoices.Items[0].ToString()!.Contains("Chatterbox Turbo"),"Chatterbox voice choices missing");
            SmokeAssert(SmokeControls<System.Windows.Controls.CheckBox>(panel).Any(x=>x.Content.ToString()=="Automatically read new replies"),"Auto-read checkbox missing");
            ReadReplyClick(this,new());SmokeAssert(speechWindow==null,"Voice panel did not toggle closed");
            downloadsDismissedAt=DateTime.MinValue;DownloadsClick(this,new());await Task.Delay(100);
            SmokeAssert(downloadPopup?.IsOpen==true,"Downloads did not open");DownloadsClick(this,new());SmokeAssert(downloadPopup?.IsOpen==false,"Downloads did not close");
            menuDismissedAt=DateTime.MinValue;MenuClick(this,new());SmokeAssert(moreMenu?.IsOpen==true,"Menu did not open");
            SmokeAssert(moreMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Any(x=>x.Header.ToString()!.Contains("Auto-read new replies: OFF")),"Auto-read state missing");
            MenuClick(this,new());SmokeAssert(moreMenu.IsOpen==false,"Menu did not close");
            int requests=0;void Requested()=>requests++;companion.ReadReplyRequested+=Requested;
            reading=new CancellationTokenSource(); // Count the bridge request without synthesizing fixture text.
            try
            {
                await companion.InspectRiggedAvatarAsync("paused=false;hostRunning=true;controls.reactions.checked=true;document.body.classList.remove('editing');voice.pause();lastLipRead=-10000;const lipRect=canvas.getBoundingClientRect();canvas.dispatchEvent(new PointerEvent('pointerdown',{clientX:lipRect.left+lipRect.width*630/1254,clientY:lipRect.top+lipRect.height*558/1254,bubbles:true}));true");
                await Task.Delay(150);SmokeAssert(requests==1,"Lip gesture did not reach native read command");
                SmokeAssert(reading.IsCancellationRequested,"Lip click did not cancel speech preparation");
                reading.Dispose();reading=new CancellationTokenSource();
                await companion.InspectRiggedAvatarAsync("(()=>{const original=voiceActive;try{voiceActive=()=>true;lastLipRead=-10000;const r=canvas.getBoundingClientRect();canvas.dispatchEvent(new PointerEvent('pointerdown',{clientX:r.left+r.width*630/1254,clientY:r.top+r.height*558/1254,bubbles:true}));}finally{voiceActive=original;}return true;})()");
                await Task.Delay(150);SmokeAssert(requests==2&&reading.IsCancellationRequested,"Lip click was blocked during active speech");
                var brow=await companion.InspectRiggedAvatarAsync("(()=>{const f=window.faceAnimationState,t=window.touchReactionState,translate=ctx.translate;function sample(closure,left,right){window.faceAnimationState={closure};window.touchReactionState={leftClosure:left,rightClosure:right};const moves=[];ctx.translate=function(x,y){moves.push([x,y]);return translate.call(this,x,y);};drawBrows();return moves;}try{const rest=sample(0,0,0),both=sample(1,0,0),one=sample(0,1,0);return Math.abs(both[0][1]-rest[0][1]-.9)<.001&&Math.abs(both[2][1]-rest[2][1]-.9)<.001&&one[0][1]===rest[0][1]&&Math.abs(one[2][1]-rest[2][1]-4)<.001;}finally{ctx.translate=translate;window.faceAnimationState=f;window.touchReactionState=t;}})()");
                SmokeAssert(brow=="true","Subtle symmetric/one-eye brow motion failed");
            }
            finally {companion.ReadReplyRequested-=Requested;reading?.Dispose();reading=null;}
            return new {companionToggle=true,voiceToggle=true,downloadsToggle=true,menuToggle=true,autoReadVisible=true,lipRead=true,blinkBrows=true};
        });

        await Check("Logo ghost on renderer failure", async () =>
        {
            companion!.Reveal(true);companion.SetCollapsed(false);
            var failure=typeof(CompanionWindow).GetField("rigFailed",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            failure.SetValue(companion,true);companion.SetActivity("idle");
            try
            {
                SmokeAssert(((FrameworkElement)companion.FindName("DefaultAvatar")).Visibility==Visibility.Visible,"Logo ghost fallback missing.");
                foreach(var name in new[]{"RiggedAvatar","EyeTrackingAvatar","CustomAvatar"})
                    SmokeAssert(((FrameworkElement)companion.FindName(name)).Visibility==Visibility.Collapsed,"Unexpected image fallback: "+name);
                CaptureElement(companion,Path.Combine(output,"logo-ghost-failure.png"));
            }
            finally { failure.SetValue(companion,false);companion.SetActivity("idle"); }
            await Task.Delay(150);
            return new { logoGhost=true, oldPortraitHidden=true };
        });

        await Check("Read-aloud playback, speech priority and cancellation", async () =>
        {
            companion!.Reveal(true); companion.SetCollapsed(false); await Task.Delay(200);
            SmokeAssert(CompanionWindow.DesktopPosition(0,-1920,3840)==.5,"Two-monitor seam should map to the listening center.");
            SmokeAssert(CompanionWindow.DesktopPosition(-1920,-1920,3840)==0 && CompanionWindow.DesktopPosition(1920,-1920,3840)==1,"Virtual desktop edges mapped incorrectly.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("voicePan(.5,.5,.8)===0 && voicePan(0,.5,.8)===-.8 && voicePan(1,.5,.8)===.8 && voicePan(.7,.7,1)===0") == "true", "Directional voice calibration failed.");
            await companion.InspectRiggedAvatarAsync("atmosphere.spatial=false;updateSpatial();true");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("spatialState.pan===0") == "true","Disabled directional voice isn't centered.");
            await companion.InspectRiggedAvatarAsync("atmosphere.spatial=true;atmosphere.wind=true;controls.autoWind.checked=false;controls.wind.value=-4;true");
            await Task.Delay(100);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("windCueState.visible && windCueState.wind===-4") == "true","Wind indicator direction disagrees with simulation.");
            CaptureElement(companion,Path.Combine(output,"wind-cues.png"));
            await companion.InspectRiggedAvatarAsync("controls.wind.value=0;true");await Task.Delay(100);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("windCueState.visible && windCueState.wind===0") == "true","Particles should keep falling in calm air.");
            foreach(var particle in new[]{"leaves","blossoms","dust","rain","mist"})
            {
                await companion.InspectRiggedAvatarAsync($"particleSettings.type='{particle}';particleSettings.count=40;true");await Task.Delay(70);
                string layers=particle=="mist"?"particleState.front===0 && particleState.back===40":"particleState.front===4 && particleState.back===36";
                SmokeAssert(await companion.InspectRiggedAvatarAsync(layers+" && particleState.count===40") == "true","Particle layers or density incorrect.");
                CaptureElement(companion,Path.Combine(output,"particles-"+particle+".png"));
            }
            await companion.InspectRiggedAvatarAsync("atmosphere.wind=false;true");await Task.Delay(70);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("!windCueState.visible") == "true","Particle visibility toggle failed.");
            await companion.InspectRiggedAvatarAsync("atmosphere.wind=true;particleSettings.type='blossoms';particleSettings.count=32;true");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("particleEdgeFade(0,.5)===0 && particleEdgeFade(1,.5)===0 && particleEdgeFade(.5,0)===0 && particleEdgeFade(.5,.5)===1") == "true","Particle edge fade failed.");
            await companion.InspectRiggedAvatarAsync("document.body.classList.add('editing');selectSettingsPage('Particles');true");
            await Task.Delay(100);CaptureElement(companion,Path.Combine(output,"compact-particle-settings.png"));
            SmokeAssert(await companion.InspectRiggedAvatarAsync("settingsAside.querySelectorAll('.settingsPage:not([hidden])').length===1 && settingsAside.scrollWidth<=settingsAside.clientWidth+1") == "true","Settings overlap or overflow.");
            await companion.InspectRiggedAvatarAsync("document.body.classList.remove('editing');true");
            await companion.InspectRiggedAvatarAsync("controls.autoWind.checked=true;controls.wind.value=4;true");
            await companion.InspectRiggedAvatarAsync("voice.muted=false;true");
            using var sample=JsonDocument.Parse(await companion.InspectRiggedAvatarAsync("builtInVoices[1]"));
            var playback=companion.PlaySpeech(sample.RootElement,CancellationToken.None);
            await Task.Delay(350);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("spatialContext.state==='running' && spatialPanner instanceof StereoPannerNode") == "true","Stereo audio graph didn't start.");
            SmokeAssert(await companion.InspectRiggedAvatarAsync("voiceActive() && speechPose()>=0 && selectedExpression()===-1") == "true", "Speech failed to control the mouth.");
            await companion.InspectRiggedAvatarAsync("voice.currentTime=voice.duration-.08;true");
            await playback.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel=new CancellationTokenSource();
            playback=companion.PlaySpeech(sample.RootElement,cancel.Token);await Task.Delay(150);cancel.Cancel();
            try { await playback; throw new InvalidOperationException("Cancellation was ignored."); } catch(OperationCanceledException) { }
            await Task.Delay(100);
            SmokeAssert(await companion.InspectRiggedAvatarAsync("voice.paused") == "true", "Stop left audio running.");
            var chunks=SpeechChunks("Hello there. "+new string('x',650)+" We have 42 messages!");
            SmokeAssert(chunks.All(c=>c.Length<=300) && string.Concat(chunks).Contains(new string('x',650)), "Long reply chunking lost content.");
            return new { playback=true, completion=true, cancellation=true, mouthPriority=true, boundedChunks=true };
        });

        if (Environment.GetCommandLineArgs().Contains("--voice-test"))
        await Check("Installed local voice worker", async () =>
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result=await localSpeech.Generate("Hello there. We can try this together.","chatterbox-turbo",PortablePaths.DefaultVoice,timeout.Token);
            SmokeAssert(result.GetProperty("engine").GetString()=="chatterbox-turbo", "Custom voice fell back unexpectedly.");
            SmokeAssert(result.GetProperty("bundle").GetProperty("cues").GetArrayLength()>0,"No mouth timing.");
            await companion!.InspectRiggedAvatarAsync("voice.muted=false;true");
            await companion.PlaySpeech(result.GetProperty("bundle"),timeout.Token);
            ReadReplyClick(this,new());
            for(int i=0;i<30&&speechWindow==null;i++)await Task.Delay(100);
            SmokeAssert(speechWindow!=null,"Read panel did not open.");
            CaptureElement(speechWindow!,Path.Combine(output,"read-aloud.png"));
            var panel=(FrameworkElement)speechWindow!.Content;
            var editor=SmokeControls<System.Windows.Controls.TextBox>(panel).Single();
            editor.Text="Hello there. This is a portable voice test.";
            speechReference=PortablePaths.DefaultVoice;
            var readButton=SmokeControls<System.Windows.Controls.Button>(panel).Single(b=>b.Content.ToString()=="Read");
            readButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for(int i=0;i<600&&!readButton.IsEnabled;i++)await Task.Delay(100);
            var status=speechPanelStatus!.Text;
            SmokeAssert(readButton.IsEnabled && status=="Finished.","Full Read action: "+status);
            speechWindow!.Close();
            localSpeech.Dispose();
            return new { customVoice=true, localWorker=true, timing=true };
        });

        await Check("Companion docking and foreground-aware bubbles", async () =>
        {
            companion!.ConnectToQuiet(this,false);
            // A maximized/minimized fixture cannot test a 16-DIP window drag.
            // Restore an explicit movable, visible state before attaching.
            WindowState=WindowState.Normal;Show();await Task.Delay(150);
            companion.SetPlacement(true);companion.Reveal();companion.SetCollapsed(false);companion.ApplyContext(true);
            companion.Snap("Bottom right");await Task.Delay(150);
            double original=Left,avatarLeft=companion.Left;
            Left+=16;await Task.Delay(100);
            double attachedTravel=companion.Left-avatarLeft;Left=original;
            SmokeAssert(Math.Abs(attachedTravel-16)<2,$"Attached companion did not follow Quiet (travel {attachedTravel:0.00}, state {WindowState})");
            companion.SetSpokenText("This sentence should only appear when Quiet is behind another app.");
            companion.ApplyContext(true);SmokeAssert(companion.IndicatorKind=="none","Attached speech text was duplicated");
            companion.ApplyContext(false);SmokeAssert(companion.IsVisible,"Attached companion disappeared in background");
            var quietState=WindowState;WindowState=WindowState.Minimized;companion.ApplyContext(false);SmokeAssert(!companion.IsVisible,"Attached companion stayed visible while minimized");WindowState=quietState;
            companion.SetPlacement(false);companion.ApplyContext(false);
            companion.SetSpokenText("Hello there. I can keep you company while you work.");
            SmokeAssert(companion.IsVisible && companion.IndicatorKind=="speech","Desktop background speech bubble missing");
            await Task.Delay(150);CaptureElement(companion.IndicatorSurface!,Path.Combine(output,"desktop-speech-bubble.png"));
            companion.ApplyContext(true);SmokeAssert(companion.IndicatorKind=="none","Bubble did not disappear when Quiet returned");
            companion.SetSpokenText("");companion.SetActivity("thinking");
            SmokeAssert(companion.IndicatorKind=="thinking","Thinking indicator missing");
            await Task.Delay(150);CaptureElement(companion.IndicatorSurface!,Path.Combine(output,"thinking-dots.png"));
            companion.SetActivity("idle");SmokeAssert(companion.IndicatorKind=="none","Thinking indicator stayed after completion");
            companion.SetPlacement(true);companion.ApplyContext(true);
            CaptureElement(companion,Path.Combine(output,"transparent-companion.png"));
            return new {attachedFollows=true,foregroundVisibility=true,desktopSpeechOnly=true,thinkingDots=true};
        });

        await Check("Right-click opens unified settings and Style controls update the avatar", async () =>
        {
            WindowState=WindowState.Normal;Show();companion!.Reveal(true);companion.SetCollapsed(false);await Task.Delay(150);
            await companion.InspectRiggedAvatarAsync("paused=false;hostRunning=true;const pendantRect=canvas.getBoundingClientRect();canvas.dispatchEvent(new MouseEvent('contextmenu',{clientX:pendantRect.left+pendantRect.width*633/1254,clientY:pendantRect.top+pendantRect.height*820/1254,bubbles:true}));true");
            for(int i=0;i<20&&companion.CompanionMenu?.IsOpen!=true;i++)await Task.Delay(50);
            SmokeAssert(companion.CompanionMenu?.IsOpen==true,"Right-click menu missing"); ((System.Windows.Controls.MenuItem)companion.CompanionMenu!.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); companion.CompanionMenu.IsOpen=false; Window? settingsWindow=null;
            for(int i=0;i<30&&settingsWindow==null;i++){await Task.Delay(100);settingsWindow=Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>w.Title=="Quiet settings · Companion");}
            SmokeAssert(settingsWindow!=null,"Menu did not open settings");
            try
            {
                var tabs=(System.Windows.Controls.TabControl)settingsWindow!.Content;
                SmokeAssert(tabs.Items.Count==3,"Settings tabs missing");
                CaptureElement(settingsWindow,Path.Combine(output,"companion-behaviour-settings.png"));
                tabs.SelectedIndex=1;
                var style=(System.Windows.Controls.StackPanel)((System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)tabs.Items[1]).Content).Content;
                for(int i=0;i<50&&style.Children.OfType<System.Windows.Controls.Border>().Select(b=>b.Child).OfType<System.Windows.Controls.Expander>().Select(e=>e.Content).OfType<System.Windows.Controls.StackPanel>().SelectMany(p=>p.Children.OfType<System.Windows.Controls.ComboBox>()).Count()==0;i++)await Task.Delay(100);
                var cap=style.Children.OfType<System.Windows.Controls.Border>().Select(b=>b.Child).OfType<System.Windows.Controls.Expander>().Select(e=>e.Content).OfType<System.Windows.Controls.StackPanel>().SelectMany(p=>p.Children.OfType<System.Windows.Controls.ComboBox>()).FirstOrDefault();
                SmokeAssert(cap!=null,"Style controls failed to load");
                var original=cap!.SelectedIndex;cap.SelectedIndex=original==0?1:0;await Task.Delay(150);
                var selected=cap.SelectedValue?.ToString();
                var actual=await companion.InspectRiggedAvatarAsync("document.getElementById('accessory-cap').value");
                SmokeAssert(JsonSerializer.Deserialize<string>(actual)==selected,"Native Style selection did not reach avatar");
                cap.SelectedIndex=original;await Task.Delay(100);
                CaptureElement(settingsWindow,Path.Combine(output,"companion-style-settings.png"));
            }
            finally{settingsWindow?.Close();}
            return new {rightClickSettings=true,threeTabs=true,liveStyleControl=true};
        });

        await Check("Polished panels keep motion tests open and playback reachable", async () =>
        {
            companion!.ShowBehaviourSettings(this,1);
            var settingsWindow=Application.Current.Windows.OfType<Window>().Single(w=>w.Title=="Quiet settings · Companion");
            try
            {
                var tabs=(System.Windows.Controls.TabControl)settingsWindow.Content;
                var scroll=(System.Windows.Controls.ScrollViewer)((System.Windows.Controls.TabItem)tabs.Items[1]).Content;
                for(int i=0;i<50&&!SmokeControls<System.Windows.Controls.Button>(scroll).Any(b=>b.Tag?.ToString()=="rigBounce");i++)await Task.Delay(50);
                var chest=SmokeControls<System.Windows.Controls.Expander>(scroll).Single(e=>e.Header.ToString()=="Chest motion");
                SmokeControls<System.Windows.Controls.Expander>(scroll).Single(e=>e.Header.ToString()=="Appearance & accessories").IsExpanded=false;
                chest.IsExpanded=true;settingsWindow.UpdateLayout();chest.BringIntoView();await Task.Delay(80);
                double offset=scroll.VerticalOffset;
                string values=await companion.InspectRiggedAvatarAsync("JSON.stringify(['strength','downFlex','volumeDepth','return','settle'].map(id=>document.getElementById(id).value))");
                foreach(string id in new[]{"rigBounce","rigDown","rigContact"})
                {
                    var button=SmokeControls<System.Windows.Controls.Button>(scroll).Single(b=>b.Tag?.ToString()==id);
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Task.Delay(120);
                    SmokeAssert(chest.IsExpanded&&SmokeControls<System.Windows.Controls.Expander>(scroll).Contains(chest),"Motion test rebuilt or collapsed the section.");
                    SmokeAssert(Math.Abs(offset-scroll.VerticalOffset)<1,"Motion test lost the scroll position.");
                    SmokeAssert(await companion.InspectRiggedAvatarAsync("v2Rig.motion.nodes.some(n=>Math.abs(n.y)+Math.abs(n.x)>1)")=="true","Motion test did not excite the avatar.");
                }
                SmokeAssert(await companion.InspectRiggedAvatarAsync("JSON.stringify(['strength','downFlex','volumeDepth','return','settle'].map(id=>document.getElementById(id).value))")==values,"Tests changed the accepted motion settings.");
                CaptureElement(settingsWindow,Path.Combine(output,"polished-chest-settings.png"));
                var movement=SmokeControls<System.Windows.Controls.Slider>(chest).Single(c=>c.Tag?.ToString()=="strength");
                double beforeKey=movement.Value;System.Windows.Controls.Slider.DecreaseSmall.Execute(null,movement);await Task.Delay(80);
                SmokeAssert(Math.Abs(movement.Value-(beforeKey-movement.TickFrequency))<.0001,"Slider keyboard step ignored its setting interval.");movement.Value=beforeKey;await Task.Delay(80);
                var look=SmokeControls<System.Windows.Controls.Expander>(scroll).Single(e=>e.Header.ToString()=="Appearance & accessories");look.IsExpanded=true;scroll.ScrollToTop();settingsWindow.UpdateLayout();
                var cap=SmokeControls<System.Windows.Controls.ComboBox>(look).Single(c=>c.Tag?.ToString()=="accessory-cap");
                var previous=cap.SelectedIndex;cap.SelectedIndex=previous==0?1:0;await Task.Delay(80);
                SmokeControls<System.Windows.Controls.Button>(look).Single(b=>b.Tag?.ToString()=="clearAccessories").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Task.Delay(150);
                SmokeAssert(chest.IsExpanded&&look.IsExpanded,"Reset action closed another section.");
                SmokeAssert(cap.SelectedValue?.ToString()==JsonSerializer.Deserialize<string>(await companion.InspectRiggedAvatarAsync("document.getElementById('accessory-cap').value")),"Reset action left the displayed selection stale.");
                cap.SelectedIndex=previous;await Task.Delay(80);cap.IsDropDownOpen=true;await Task.Delay(80);
                SmokeAssert(cap.Template.FindName("Focus",cap)!=null,"Readable themed dropdown was not applied.");
                CaptureElement(settingsWindow,Path.Combine(output,"polished-dropdown.png"));
                var popup=(System.Windows.Controls.Primitives.Popup)cap.Template.FindName("PART_Popup",cap);
                CaptureElement((FrameworkElement)popup.Child,Path.Combine(output,"polished-dropdown-options.png"));cap.IsDropDownOpen=false;
                SmokeAssert(settingsWindow.WindowStyle==WindowStyle.None,"Settings retained the system titlebar.");
                tabs.SelectedIndex=2;settingsWindow.UpdateLayout();CaptureElement(settingsWindow,Path.Combine(output,"polished-interactions.png"));
            }
            finally{settingsWindow.Close();}
            ReadReplyClick(this,new());for(int i=0;i<40&&speechWindow==null;i++)await Task.Delay(50);
            try
            {
                SmokeAssert(speechWindow!=null,"Read aloud failed to open.");
                CaptureElement(speechWindow!,Path.Combine(output,"polished-read-aloud.png"));
                speechWindow!.Height=480;speechWindow.Width=470;
                SmokeControls<System.Windows.Controls.Expander>(speechWindow).Single(e=>e.Header.ToString()=="Voice sample & tips").IsExpanded=true;
                speechWindow.UpdateLayout();
                var play=SmokeControls<System.Windows.Controls.Button>(speechWindow).Single(b=>b.Content.ToString()=="Read");
                var point=play.TranslatePoint(new Point(0,0),speechWindow);
                SmokeAssert(point.Y>=44&&point.Y+play.ActualHeight<speechWindow.ActualHeight,"Playback footer was clipped in the small window.");
                var slider=SmokeControls<System.Windows.Controls.Slider>(speechWindow).Single();
                slider.ApplyTemplate();SmokeAssert(slider.Template.FindName("PART_Track",slider) is System.Windows.Controls.Primitives.Track,"Themed slider is missing its functional track.");
                SmokeAssert(speechWindow.WindowStyle==WindowStyle.None,"Read aloud retained the system titlebar.");
                CaptureElement(speechWindow,Path.Combine(output,"polished-read-aloud-small.png"));
            }
            finally{speechWindow?.Close();}
            return new{stableExpandedSections=true,stableScroll=true,actualMotionTests=true,resetSynchronizes=true,darkChrome=true,resizableReading=true,stickyPlayback=true};
        });

        await Check("Voice output devices: routing, persistence, unavailable fallback and capture guard", async () =>
        {
            companion!.Reveal();companion.SetCollapsed(false);
            var devices=await companion.GetAudioOutputs();
            SmokeAssert(devices.GetProperty("supported").GetBoolean(),"Web Audio output routing is unsupported.");
            var choices=devices.GetProperty("devices");SmokeAssert(choices.GetArrayLength()>0,"No named output devices enumerated.");
            var first=choices[0];string device=first.GetProperty("id").GetString()!,label=first.GetProperty("label").GetString()!;
            string previousDevice=speechOutputDevice,previousLabel=speechOutputLabel,preferencesPath=Path.Combine(dataRoot,"voice.json");
            string? preferences=File.Exists(preferencesPath)?await File.ReadAllTextAsync(preferencesPath):null;
            using var sample=JsonDocument.Parse(await companion.InspectRiggedAvatarAsync("builtInVoices[1]"));
            try
            {
                ReadReplyClick(this,new());for(int i=0;i<40&&speechWindow==null;i++)await Task.Delay(50);
                var audioPicker=SmokeControls<System.Windows.Controls.ComboBox>(speechWindow!).Single(c=>c.Tag?.ToString()=="speech-output");
                for(int i=0;i<40&&audioPicker.Items.Count<=1;i++)await Task.Delay(50);
                SmokeAssert(audioPicker.Items.Count==choices.GetArrayLength()+1,"Output selector omitted connected devices or Windows default.");
                audioPicker.SelectedValue=device;await Task.Delay(150);
                SmokeAssert(audioPicker.SelectionBoxItem?.ToString()==label,"Selected output displayed internal data instead of its friendly name.");
                SmokeAssert(await companion.InspectRiggedAvatarAsync("spatialContext.sinkId===requestedVoiceSink&&requestedVoiceSink==="+JsonSerializer.Serialize(device))=="true","UI selection did not route the actual audio context.");
                speechOutputDevice="";speechOutputLabel="";LoadVoicePreferences();SmokeAssert(speechOutputDevice==device&&speechOutputLabel==label,"Output preference did not reload.");
                CaptureElement(speechWindow!,Path.Combine(AppContext.BaseDirectory,"test-results","read-aloud-output-device.png"));
                speechWindow!.Close();
                ReadReplyClick(this,new());for(int i=0;i<40&&speechWindow==null;i++)await Task.Delay(50);
                audioPicker=SmokeControls<System.Windows.Controls.ComboBox>(speechWindow!).Single(c=>c.Tag?.ToString()=="speech-output");
                for(int i=0;i<40&&audioPicker.SelectedValue?.ToString()!=device;i++)await Task.Delay(50);
                SmokeAssert(audioPicker.SelectedValue?.ToString()==device,"Reopened selector forgot its choice.");
                var tested=await companion.TestAudioOutput(true);SmokeAssert(tested.GetProperty("actual").GetString()==device,"Speaker test used a different output.");
                await companion.InspectRiggedAvatarAsync("voice.muted=true;true");
                var routeFixture=System.Text.Json.Nodes.JsonNode.Parse(sample.RootElement.GetRawText())!;
                using(var wav=new MemoryStream())
                {
                    using(var writer=new BinaryWriter(wav,System.Text.Encoding.ASCII,true))
                    {
                        int bytes=16000*2*20;writer.Write("RIFF"u8);writer.Write(36+bytes);writer.Write("WAVEfmt "u8);writer.Write(16);
                        writer.Write((short)1);writer.Write((short)1);writer.Write(16000);writer.Write(32000);writer.Write((short)2);writer.Write((short)16);
                        writer.Write("data"u8);writer.Write(bytes);writer.Write(new byte[bytes]);
                    }
                    routeFixture["audio"]="data:audio/wav;base64,"+Convert.ToBase64String(wav.ToArray());routeFixture["duration"]=20;
                }
                using var routeSample=JsonDocument.Parse(routeFixture.ToJsonString());
                var playback=companion.PlaySpeech(routeSample.RootElement,CancellationToken.None);await Task.Delay(350);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("voiceActive()&&voice.currentTime>.1&&spatialContext.sinkId==="+JsonSerializer.Serialize(device))=="true","Speech failed to play on the selected sink with a running mouth clock.");
                double beforeSwitch=double.Parse(await companion.InspectRiggedAvatarAsync("voice.currentTime"),System.Globalization.CultureInfo.InvariantCulture);
                await companion.SetAudioOutput("","");await Task.Delay(150);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("voiceActive()&&voice.currentTime>="+beforeSwitch.ToString(System.Globalization.CultureInfo.InvariantCulture)+"&&spatialContext.sinkId===''")=="true","Switching outputs restarted or stopped speech.");
                companion.StopSpeech();try{await playback;}catch(OperationCanceledException){}
                var unavailable=await companion.SetAudioOutput("quiet-missing-device","Disconnected speakers");
                SmokeAssert(unavailable.GetProperty("fallback").GetBoolean()&&unavailable.GetProperty("actual").GetString()=="","Unavailable output did not fall back to Windows default.");
                await companion.InspectRiggedAvatarAsync("window.originalQuietDevices=navigator.mediaDevices.enumerateDevices.bind(navigator.mediaDevices);navigator.mediaDevices.enumerateDevices=async()=>[];true");
                try
                {
                    var disconnected=await companion.SetAudioOutput(device,label);SmokeAssert(disconnected.GetProperty("fallback").GetBoolean(),"Disconnection did not fall back.");
                }
                finally{await companion.InspectRiggedAvatarAsync("navigator.mediaDevices.enumerateDevices=window.originalQuietDevices;delete window.originalQuietDevices;true");}
                var restored=await companion.SetAudioOutput(device,label);SmokeAssert(!restored.GetProperty("fallback").GetBoolean()&&restored.GetProperty("actual").GetString()==device,"Reconnected output did not restore.");
                SmokeAssert(await companion.InspectRiggedAvatarAsync("navigator.mediaDevices.getUserMedia.toString().includes('This avatar only plays audio.')")=="true","Input capture guard was not installed.");
                await companion.InspectRiggedAvatarAsync("window.quietCaptureGuardResult='pending';navigator.mediaDevices.getUserMedia({audio:true}).then(()=>window.quietCaptureGuardResult='allowed',e=>window.quietCaptureGuardResult=e.name);true");await Task.Delay(50);
                SmokeAssert(await companion.InspectRiggedAvatarAsync("window.quietCaptureGuardResult==='NotAllowedError'&&Object.getOwnPropertyDescriptor(navigator.mediaDevices,'getUserMedia').writable===false")=="true","Output discovery enabled input capture.");
                return new{devices=choices.GetArrayLength(),realSinkSelection=true,testChime=true,uiAndReload=true,switchDuringPlayback=true,mediaClockRetained=true,disconnectedFallback=true,reconnection=true,noInputCapture=true};
            }
            finally
            {
                companion.StopSpeech();speechWindow?.Close();speechOutputDevice=previousDevice;speechOutputLabel=previousLabel;
                await companion.SetAudioOutput(previousDevice,previousLabel);
                if(preferences!=null)await File.WriteAllTextAsync(preferencesPath,preferences);else if(File.Exists(preferencesPath))File.Delete(preferencesPath);
            }
        });

        if (!Environment.GetCommandLineArgs().Contains("--offline"))
        await Check("Live ChatGPT page and screenshot", async () =>
        {
            var navigation = await SmokeNavigateAsync(Browser.CoreWebView2, "https://chatgpt.com/");
            await Task.Delay(2500);
            string title = Browser.CoreWebView2.DocumentTitle;
            string body = JsonSerializer.Deserialize<string>(await Browser.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText.slice(0,500) : ''")) ?? "";
            await using (var stream = File.Create(Path.Combine(output, "chatgpt.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            // WPF's renderer excludes WebView2's native HWND. Capture the page separately above.
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            await using (var stream = File.Create(Path.Combine(output, "shell.png"))) encoder.Save(stream);
            SmokeAssert(title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase), $"Live page did not identify as ChatGPT. Title: {title}; HTTP: {navigation.HttpStatusCode}; text: {body}");
            SmokeAssert(body.Length > 20, "Live page body is empty.");
            return new { title, httpStatus = navigation.HttpStatusCode, bodyExcerpt = body, page = "chatgpt.png", shell = "shell.png", note = "No login or conversation submission performed. Shell capture excludes native web content." };
        });

        await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            passed = failures == 0,
            failures,
            checks = results
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (Environment.GetCommandLineArgs().Contains("--ui-preview"))
        {
            await SmokeNavigateAsync(Browser.CoreWebView2, "https://quiet.test/index.html");
            companion!.Reveal(); companion.SetActivity("idle");
            return;
        }
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    private static IEnumerable<T> SmokeControls<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T match)yield return match;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var found in SmokeControls<T>(child))yield return found;
    }

    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void SmokeAssert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Task<CoreWebView2NavigationCompletedEventArgs> SmokeNavigateAsync(CoreWebView2 core, string address) =>
        SmokeNavigationAsync(core, () => { core.Navigate(address); return Task.CompletedTask; });

    private static async Task<CoreWebView2NavigationCompletedEventArgs> SmokeNavigationAsync(CoreWebView2 core, Func<Task> start)
    {
        var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = (_, e) =>
        {
            if (e.IsSuccess) completed.TrySetResult(e);
            else completed.TrySetException(new IOException("Navigation failed: " + e.WebErrorStatus));
        };
        core.NavigationCompleted += handler;
        try { await start(); return await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { core.NavigationCompleted -= handler; }
    }
}





