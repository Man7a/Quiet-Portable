using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace QuietGPT;

public partial class MainWindow
{
    private Window? voiceSetupWindow;
    private Process? voiceSetupProcess;
    private bool voiceSetupBusy;

    private void AddVoiceSetupControls(Panel panel)
    {
        var hint=new TextBlock { Text=PortablePaths.VoiceReady?"Turbo is ready · models load only when speaking.":"Voice is optional. Your companion works without it.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8) };
        var setup=new Button { Content=PortablePaths.VoiceReady?"Manage voice…":"Set up voice…",Tag="voice-setup",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12) };
        setup.Click+=(_,_)=>OpenVoiceSetup();panel.Children.Add(hint);panel.Children.Add(setup);
    }
    private void OpenVoiceSetup()
    {
        if(voiceSetupWindow!=null){voiceSetupWindow.Activate();return;}
        var panel=new StackPanel {Margin=new Thickness(20)};
        panel.Children.Add(new TextBlock{Text="Give Quiet a voice",FontSize=22,Margin=new Thickness(0,0,0,12)});
        panel.Children.Add(new TextBlock{Text="Install Chatterbox Turbo for local speech. Requires a supported NVIDIA GPU, a current driver, internet for setup, and at least 12 GB of free disk space. Downloads stay inside this portable folder. No administrator access is needed.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        panel.Children.Add(new TextBlock{Text="Downloads come from GitHub, PyPI, PyTorch and Hugging Face. Setup verifies a short voice sample before enabling speech. Your chats and voice recordings are not uploaded for synthesis.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        var status=new TextBlock{Text=PortablePaths.VoiceReady?"Voice is installed. You can test it or repair the installation.":"Ready to install · Quiet also works silently.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};
        var progress=new ProgressBar{Height=5,IsIndeterminate=false,Visibility=Visibility.Collapsed,Margin=new Thickness(0,0,0,10)};
        var details=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=220,Text="Setup progress will appear here."};
        var buttons=new WrapPanel{Margin=new Thickness(0,12,0,0)};
        var install=new Button{Content=PortablePaths.VoiceReady?"Repair voice":"Install voice",Tag="install-voice",Margin=new Thickness(0,0,8,0)};
        var cancel=new Button{Content="Cancel",IsEnabled=false,Margin=new Thickness(0,0,8,0)};
        var test=new Button{Content="Test voice",IsEnabled=PortablePaths.VoiceReady,Margin=new Thickness(0,0,8,0)};
        string existingTurboFolder="";
        var reuse=new Button{Content="Use existing Turbo files…",Margin=new Thickness(0,0,8,0)};
        reuse.Click+=(_,_)=>{var picker=new Microsoft.Win32.OpenFolderDialog{Title="Choose the folder containing Turbo's safetensors, tokenizer and default voice files"};if(picker.ShowDialog(voiceSetupWindow)==true){existingTurboFolder=picker.FolderName;status.Text="Existing Turbo files selected. Install voice will verify and reuse matching files.";}};
        buttons.Children.Add(install);buttons.Children.Add(cancel);buttons.Children.Add(test);
        buttons.Children.Add(reuse);
        panel.Children.Add(status);panel.Children.Add(progress);
        panel.Children.Add(new Expander{Header="Setup details",Content=details,Margin=new Thickness(0,4,0,4)});panel.Children.Add(buttons);
        var window=new Window{Title="Voice setup · Quiet Portable",Owner=this,Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},Width=620,Height=470,MinHeight=350,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        QuietPanel.Apply(window);install.Style=(Style)window.FindResource("PrimaryButton");voiceSetupWindow=window;
        bool canceled=false,closed=false;
        void Cancel(){canceled=true;try{if(voiceSetupProcess is {HasExited:false})voiceSetupProcess.Kill(true);}catch {}}
        cancel.Click+=(_,_)=>Cancel();
        window.Closed+=(_,_)=>{closed=true;Cancel();voiceSetupWindow=null;};
        install.Click+=async(_,_)=>
        {
            if(voiceSetupBusy)return;
            voiceSetupBusy=true;canceled=false;install.IsEnabled=false;reuse.IsEnabled=false;test.IsEnabled=false;cancel.IsEnabled=true;
            progress.Visibility=Visibility.Visible;progress.IsIndeterminate=true;details.Text="";
            status.Text="Installing voice · you can cancel and retry later.";
            var log=new StringBuilder();
            void Append(string? line)
            {
                if(line==null)return;
                lock(log){if(log.Length<4_000_000)log.AppendLine(line);}
                _=Dispatcher.BeginInvoke(new Action(()=>{if(closed)return;
                    if(line.StartsWith("Downloading verified")||line.StartsWith("Preparing portable")||line.StartsWith("Installing Turbo")||line.StartsWith("Downloading Chatterbox")||line.StartsWith("Testing offline"))status.Text=line;
                    details.AppendText(line+Environment.NewLine);if(details.Text.Length>60000)details.Text=details.Text[^40000..];details.ScrollToEnd();}));
            }
            try
            {
                StopReading();await localSpeech.UnloadForSetup();
                if(canceled||closed)return;
                Directory.CreateDirectory(PortablePaths.VoiceRoot);
                File.Delete(Path.Combine(PortablePaths.VoiceRoot,"ready.json"));
                var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
                foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(AppContext.BaseDirectory,"Voice","setup-voice.ps1")})start.ArgumentList.Add(arg);
                if(existingTurboFolder.Length>0){start.ArgumentList.Add("-ExistingTurboFolder");start.ArgumentList.Add(existingTurboFolder);}
                using var process=Process.Start(start)??throw new IOException("Could not start voice setup.");
                voiceSetupProcess=process;process.OutputDataReceived+=(_,e)=>Append(e.Data);process.ErrorDataReceived+=(_,e)=>Append(e.Data);process.BeginOutputReadLine();process.BeginErrorReadLine();
                await process.WaitForExitAsync();process.WaitForExit();
                status.Text=canceled?"Canceled · downloaded files are kept for the next attempt.":process.ExitCode==0&&PortablePaths.VoiceReady?"Voice ready · choose Test voice, or close this window and press Read.":"Setup did not finish. Check the details below, then retry. Quiet still works silently.";
            }
            catch(Exception ex){status.Text="Setup failed: "+ex.Message;Append(ex.ToString());}
            finally
            {
                voiceSetupProcess=null;voiceSetupBusy=false;progress.IsIndeterminate=false;progress.Visibility=Visibility.Collapsed;
                install.IsEnabled=true;reuse.IsEnabled=true;cancel.IsEnabled=false;test.IsEnabled=PortablePaths.VoiceReady;install.Content=PortablePaths.VoiceReady?"Repair voice":"Retry installation";
                try{File.WriteAllText(Path.Combine(PortablePaths.VoiceRoot,"setup.log"),log.ToString());}catch{}
            }
        };
        test.Click+=async(_,_)=>
        {
            test.IsEnabled=false;
            try
            {
                if(companion==null)throw new IOException("Companion is not ready.");
                using var bundle=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(PortablePaths.VoiceRoot,"voice-test.json")));
                companion.UseRiggedAvatar(false);companion.Reveal(false);await companion.SetAudioOutput(speechOutputDevice,speechOutputLabel);
                await companion.PlaySpeech(bundle.RootElement,CancellationToken.None);status.Text="Voice test finished.";
            }
            catch(Exception ex){status.Text="Could not play voice test: "+ex.Message;}
            finally{test.IsEnabled=PortablePaths.VoiceReady;}
        };
        window.Show();
    }
}
