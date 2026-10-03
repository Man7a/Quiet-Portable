using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuietGPT;

public partial class MainWindow
{
    private readonly LocalSpeech localSpeech = new();
    private CancellationTokenSource? reading;
    private Window? speechWindow;
    private Button? speechPauseButton;
    private TextBlock? speechPanelStatus;
    private bool readingPaused;
    private double speechSpeed=1;
    private TaskCompletionSource<bool>? readingResume;
    private string speechProgress="";
    private void UpdateReadingControls()
    {
        if(speechPauseButton!=null){speechPauseButton.IsEnabled=reading!=null&&!reading.IsCancellationRequested;speechPauseButton.Content=readingPaused?"Resume":"Pause";}
    }
    private void SetReadingPaused(bool value)
    {
        readingPaused=value&&reading!=null&&!reading.IsCancellationRequested;
        if(readingPaused)readingResume??=new(TaskCreationOptions.RunContinuationsAsynchronously);
        else{readingResume?.TrySetResult(true);readingResume=null;}
        companion?.SetSpeechPaused(readingPaused);UpdateReadingControls();
        string message=readingPaused?"Paused · Resume to continue the same reply":speechProgress;
        if(speechPanelStatus!=null)speechPanelStatus.Text=message;
        if(message.Length>0)SetStatus(message);
    }
    private Task WaitForReadingResume(CancellationToken token) => readingPaused&&readingResume!=null?readingResume.Task.WaitAsync(token):Task.CompletedTask;

    private string speechReference = PortablePaths.DefaultVoice;
    private string speechEngine = "chatterbox-turbo";
    private static readonly string[] SpeechEngines = ["chatterbox-turbo"];
    private static string SpeechEngineName(string? engine) => "Chatterbox Turbo";
    private void CompanionSpeechVisibilityChanged()
    {
        bool hidden = companion?.IsVisible != true;
        if(hidden)StopReading();
        localSpeech.SetHidden(hidden);
    }
    private bool lipReadPending;
    private int lipReadVersion;
    private async void ReadReplyFromLips()
    {
        if(closing)return;
        if(!smokeTest && !PortablePaths.VoiceReady){OpenVoiceSetup();return;}
        if(reading!=null || lipReadPending)
        {
            lipReadVersion++;lipReadPending=false;
            StopReading();companion?.ReactToSpeechStop();
            SetStatus("Stopped · Click her lips to read again");return;
        }
        if(Browser.CoreWebView2 is not { } core || !IsCompanionOrigin(core.Source))return;
        lipReadPending=true;
        int request=++lipReadVersion;
        int epoch=autoReadEpoch;
        try
        {
            var reply=await CollectCompletedReply(core);
            var text=reply.GetProperty("text").GetString()??"";
            if(request!=lipReadVersion || epoch!=autoReadEpoch || closing)return;
            if(reply.GetProperty("state").GetString()=="streaming"){SetStatus("Reply is still being written · Click her lips when it finishes");return;}
            if(string.IsNullOrWhiteSpace(text)){SetStatus("No completed reply to read in this chat yet");return;}
            LoadVoicePreferences();
            await ReadAutomatically(text,epoch);
        }
        catch(Exception ex){SetStatus("Could not read the reply: "+ex.Message);}
        finally{if(request==lipReadVersion)lipReadPending=false;}
    }
    private void StopReading()
    {
        reading?.Cancel();
        companion?.StopSpeech();
        SetReadingPaused(false);UpdateReadingControls();
    }
    private void LoadVoicePreferences()
    {
        try
        {
            var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot,"voice.json")));
            using(saved) { var engine=saved.RootElement.GetProperty("engine").GetString(); speechEngine=SpeechEngines.Contains(engine)?engine!:"chatterbox-turbo"; speechReference=PortablePaths.LoadReference(saved.RootElement.GetProperty("reference").GetString()??speechReference); emojiExpressions=!saved.RootElement.TryGetProperty("emojiExpressions",out var faces)||faces.ValueKind!=JsonValueKind.False;emojiSounds=saved.RootElement.TryGetProperty("emojiSounds",out var sounds)&&sounds.ValueKind==JsonValueKind.True;if(saved.RootElement.TryGetProperty("speed",out var speed)&&speed.TryGetDouble(out var rate)&&double.IsFinite(rate))speechSpeed=Math.Clamp(rate,.65,1.5);if(saved.RootElement.TryGetProperty("outputDevice",out var device)&&device.ValueKind==JsonValueKind.String)speechOutputDevice=device.GetString()??"";if(saved.RootElement.TryGetProperty("outputLabel",out var label)&&label.ValueKind==JsonValueKind.String)speechOutputLabel=label.GetString()??""; }
        }
        catch { }
    }
    private async void ReadReplyClick(object sender, RoutedEventArgs e)
    {
        if (speechWindow != null) { speechWindow.Close(); return; }
        LoadVoicePreferences();
        string text = "";
        if (Browser.CoreWebView2 is { } core && IsCompanionOrigin(core.Source))
        {
            try
            {
                var source = core.Source;
                var reply=await CollectCompletedReply(core);
                if(core.Source==source)text=reply.GetProperty("text").GetString()??"";
            }
            catch { SetStatus("Could not collect the reply. You can paste text into Read aloud."); }
        }
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Read aloud", FontSize = 20, Margin = new Thickness(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text = "Review the latest reply or paste your own text.", TextWrapping = TextWrapping.Wrap });
        var editor = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 180, MaxLength = 12000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0,10,0,10) };
        editor.Background=new SolidColorBrush(Color.FromRgb(32,44,38));editor.Foreground=Brushes.WhiteSmoke;editor.Padding=new Thickness(8);
        panel.Children.Add(editor);
        panel.Children.Add(new TextBlock{Text="Voice",Margin=new Thickness(0,4,0,5)});
        var voices = new ComboBox { Tag="speech-voice", ItemsSource = new[] { "Chatterbox Turbo · NVIDIA GPU" }, SelectedIndex = Array.IndexOf(SpeechEngines, speechEngine), Margin = new Thickness(0,0,0,10) };
        panel.Children.Add(voices);AddVoiceSetupControls(panel);AddAudioOutputControls(panel);
        var autoReadBox = new CheckBox { Content="Automatically read new replies", IsChecked=autoReadEnabled, Margin=new Thickness(0,0,0,10), ToolTip="Only new replies in the current chat. Opening old chats does not read their history." };
        autoReadBox.Click += (_,_) => { if(autoReadBox.IsChecked != autoReadEnabled)ToggleAutoRead(); };
        panel.Children.Add(autoReadBox);
        var smileyBox=new CheckBox{Content="Use smileys for expressions",IsChecked=emojiExpressions,Margin=new Thickness(0,0,0,8),ToolTip="Smileys shape her eyes and brows during speech, then a brief mouth pose. Emoji names are omitted from speech."};
        var chuckleBox=new CheckBox{Content="Occasional emoji chuckles · Turbo",IsChecked=emojiSounds,Margin=new Thickness(0,0,0,10),ToolTip="Optional: at most one chuckle per read, on about one third of laughing-emoji replies. Turbo only."};
        void SyncSmileyOptions(){chuckleBox.IsEnabled=smileyBox.IsChecked==true&&voices.SelectedIndex==0;}
        smileyBox.Click+=(_,_)=>{emojiExpressions=smileyBox.IsChecked==true;SyncSmileyOptions();SaveVoicePreferences();};
        chuckleBox.Click+=(_,_)=>{emojiSounds=chuckleBox.IsChecked==true;SaveVoicePreferences();};
        voices.SelectionChanged+=(_,_)=>{if(voices.SelectedIndex>=0){speechEngine=SpeechEngines[voices.SelectedIndex];SyncSmileyOptions();SaveVoicePreferences();}};SyncSmileyOptions();panel.Children.Add(smileyBox);panel.Children.Add(chuckleBox);
        var voiceOptions=new StackPanel{Margin=new Thickness(12,0,12,12)};
        var referenceLabel = new TextBlock { Text = speechReference.Length==0?"Voice: Turbo default":"Reference: " + Path.GetFileName(speechReference), Margin = new Thickness(0,0,0,8) };
        voiceOptions.Children.Add(referenceLabel);
        var choose = new Button { Content = "Choose voice sample…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10,5,10,5) };
        choose.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Voice recordings|*.wav;*.mp3;*.flac;*.ogg" }; if(picker.ShowDialog(speechWindow)==true) { speechReference=PortablePaths.ImportVoice(picker.FileName); referenceLabel.Text="Reference: "+picker.SafeFileName; SaveVoicePreferences(); } };
        voiceOptions.Children.Add(choose);
        var resetVoice=new Button{Content="Use Quiet's voice",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,8)};
        resetVoice.Click+=(_,_)=>{speechReference=PortablePaths.DefaultVoice;referenceLabel.Text="Voice: F_Quiet.wav";SaveVoicePreferences();};voiceOptions.Children.Add(resetVoice);
        voiceOptions.Children.Add(new TextBlock { Text = "Turbo: use a clean voice sample longer than 5 seconds. You can add [chuckle], [sigh] or [laugh] to the text. Voice models unload after 30 seconds hidden and inactive.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) });
        panel.Children.Add(new Border{Child=new Expander{Header="Voice sample & tips",Content=voiceOptions},Background=new SolidColorBrush(Color.FromRgb(30,44,35)),BorderBrush=new SolidColorBrush(Color.FromRgb(57,79,65)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Margin=new Thickness(0,4,0,8)});
        var status = new TextBlock { Text = autoReadEnabled ? "Auto-read is on. New replies can play while this panel is open." : "Press Read for this text, or enable automatic reading above.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) };
        status.Foreground=new SolidColorBrush(Color.FromRgb(172,199,182));
        speechPanelStatus=status;
        var speedLabel=new TextBlock{Text=$"Speech speed · {speechSpeed:0.00}×",Margin=new Thickness(0,0,0,4)};
        var speedControl=new Slider{Minimum=.65,Maximum=1.5,Value=speechSpeed,TickFrequency=.05,SmallChange=.05,LargeChange=.15,IsSnapToTickEnabled=true,Margin=new Thickness(0,0,0,12),ToolTip="Changes playback speed while preserving voice pitch. Mouth timing follows the audio."};
        speedControl.ValueChanged+=(_,_)=>{speechSpeed=speedControl.Value;speedLabel.Text=$"Speech speed · {speechSpeed:0.00}×";companion?.SetSpeechRate(speechSpeed);SaveVoicePreferences();};
        panel.Children.Add(speedLabel);panel.Children.Add(speedControl);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button { Content = "Read", Padding = new Thickness(22,8,22,8) };
        var stop = new Button { Content = "Stop", Margin = new Thickness(10,0,0,0), Padding = new Thickness(22,8,22,8) };
        speechPauseButton=new Button{Content="Pause",Margin=new Thickness(10,0,0,0),Padding=new Thickness(22,8,22,8)};
        speechPauseButton.Click+=(_,_)=>SetReadingPaused(!readingPaused);
        buttons.Children.Add(play);buttons.Children.Add(speechPauseButton);buttons.Children.Add(stop);UpdateReadingControls();
        var footer=new StackPanel{Margin=new Thickness(20,0,20,18)};
        footer.Children.Add(status);footer.Children.Add(buttons);
        var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        layout.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        layout.Children.Add(new Border{Child=footer,BorderBrush=new SolidColorBrush(Color.FromRgb(57,79,65)),BorderThickness=new Thickness(0,1,0,0)});Grid.SetRow(layout.Children[1],1);
        speechWindow = new Window { Title="Read aloud · Quiet", Owner=this, Content=layout, Width=590, Height=850, MinHeight=480, WindowStartupLocation=WindowStartupLocation.CenterOwner };
        QuietPanel.Apply(speechWindow);
        play.Style=(Style)speechWindow.FindResource("PrimaryButton");
        speechWindow.Closed += (_,_) => { StopReading();audioOutputPanelCleanup?.Invoke();audioOutputPanelCleanup=null;speechWindow=null;speechPauseButton=null;speechPanelStatus=null; };
        stop.Click += (_,_) => { StopReading(); status.Text="Stopped."; };
        play.Click += async (_,_) =>
        {
            if(!smokeTest && !PortablePaths.VoiceReady){OpenVoiceSetup();return;}
            if (string.IsNullOrWhiteSpace(editor.Text) || companion == null) return;
            StopReading(); var cts = new CancellationTokenSource(); reading=cts;UpdateReadingControls();
            speechEngine=SpeechEngines[Math.Clamp(voices.SelectedIndex,0,SpeechEngines.Length-1)];
            var engine=speechEngine;var reference=speechReference;
            SaveVoicePreferences();
            play.IsEnabled=false;voices.IsEnabled=false;choose.IsEnabled=false;
            try
            {
                await SpeakText(editor.Text, engine, reference, cts.Token, message => status.Text=message);
            }
            catch(OperationCanceledException ex) { status.Text=cts.IsCancellationRequested?"Stopped.":"Playback interrupted: "+ex.Message+" Keep the companion visible and try Read again."; }
            catch(Exception ex) { status.Text="Could not read: "+ex.Message; StopReading(); }
            finally { cts.Cancel();if(reading==cts){reading=null;SetReadingPaused(false);}cts.Dispose();UpdateReadingControls();play.IsEnabled=true;voices.IsEnabled=true;choose.IsEnabled=true; }
        };
        speechWindow.Show();
    }
    private async Task SpeakText(string text, string engine, string reference, CancellationToken token, Action<string> report)
    {
        if(companion == null) return;
        void Progress(string message){speechProgress=message;string shown=readingPaused?"Paused · Resume to continue the same reply":message;report(shown);if(speechPanelStatus!=null)speechPanelStatus.Text=shown;}
        companion.SetSpeechRate(speechSpeed);companion.SetSpeechPaused(readingPaused);
        RecordSpeechStage("starting");
                companion.UseRiggedAvatar(false);companion.Reveal(false);companion.SetCollapsed(false);
                for(int i=0;i<100&&!companion.IsRiggedAvatarReady;i++){await Task.Delay(100,token);}
                if(!companion.IsRiggedAvatarReady)throw new IOException("The animated companion is not ready.");
                await companion.SetAudioOutput(speechOutputDevice,speechOutputLabel,token);
                var chunks = SpeechSegments(text,emojiExpressions,emojiExpressions&&emojiSounds&&engine=="chatterbox-turbo"&&Random.Shared.Next(3)==0);
                Task<JsonElement>? pending = null;
                for(int i=0;i<chunks.Count;i++)
                {
                    await WaitForReadingResume(token);
                    RecordSpeechStage("preparing");
                    Progress($"Preparing speech {i+1} of {chunks.Count}…");
                    var result=await (pending??localSpeech.Generate(chunks[i].Text,engine,reference,token));
                    token.ThrowIfCancellationRequested();
                    await WaitForReadingResume(token);
                    var actual=result.GetProperty("engine").GetString();
                    if(result.GetProperty("fallback").GetBoolean())engine=actual??"supertonic";
                    var bundle=result.GetProperty("bundle");
                    Progress($"Reading {i+1} of {chunks.Count} · {SpeechEngineName(actual)}"+(actual!=speechEngine?" (fallback)":""));
                    companion.SetSpokenText(chunks[i].Text);
                    RecordSpeechStage("playing");
                    var playback=companion.PlaySpeech(bundle,token,chunks[i].Mood);
                    pending=i+1<chunks.Count?localSpeech.Generate(chunks[i+1].Text,engine,reference,token):null;
                    // Always observe a prefetched failure, including when playback is cancelled.
                    if(pending!=null)_=pending.ContinueWith(t=>{ _=t.Exception; },TaskContinuationOptions.OnlyOnFaulted);
                    await playback;
                }
                RecordSpeechStage("finished");
                Progress("Finished.");
    }
    internal static List<string> SpeechChunks(string text)
    {
        text=Regex.Replace(text,@"https?://\S+", "link");
        text=Regex.Replace(text,@"\s+"," ").Trim();
        var chunks=new List<string>();
        while(text.Length>0)
        {
            int take=Math.Min(300,text.Length);
            var end=Regex.Match(text[..take],@"[.!?](?:\s|$)");
            if(end.Success)take=end.Index+end.Length;
            else if(take<text.Length){int space=text.LastIndexOf(' ',take-1,take);if(space>0)take=space+1;}
            var part=text[..take].Trim();if(part.Length>0)chunks.Add(part);text=text[take..].TrimStart();
        }
        return chunks;
    }
}
