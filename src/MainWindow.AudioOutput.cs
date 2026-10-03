using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuietGPT;

public partial class MainWindow
{
    private string speechOutputDevice="",speechOutputLabel="";
    private Action? audioOutputPanelCleanup;
    private sealed record SpeechOutputChoice(string Id,string Label)
    {
        public override string ToString()=>Label;
    }
    private void AddAudioOutputControls(Panel panel)
    {
        panel.Children.Add(new TextBlock{Text="Output device",Margin=new Thickness(0,2,0,5)});
        var output=new ComboBox{Tag="speech-output",SelectedValuePath="Id",DisplayMemberPath="Label",Margin=new Thickness(0,0,0,7)};
        var initialChoices=new List<SpeechOutputChoice>{new("","Windows default")};
        if(speechOutputDevice.Length>0)initialChoices.Add(new(speechOutputDevice,speechOutputLabel.Length>0?speechOutputLabel:"Saved output device"));
        output.ItemsSource=initialChoices;output.SelectedValue=speechOutputDevice;
        panel.Children.Add(output);
        var actions=new WrapPanel{Margin=new Thickness(0,0,0,6)};
        var test=new Button{Content="Test speakers",Tag="test-speakers",Margin=new Thickness(0,0,8,0)};
        var refresh=new Button{Content="Refresh devices",Tag="refresh-speakers"};actions.Children.Add(test);actions.Children.Add(refresh);panel.Children.Add(actions);
        var hint=new TextBlock{Text="Finding connected speakers…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)};
        // The scoped theme belongs to the panel window, which is created below.
        hint.Foreground=new SolidColorBrush(Color.FromRgb(171,196,181));panel.Children.Add(hint);
        bool syncing=false,refreshing=false,refreshAgain=false,closed=false;
        void ShowRoute(JsonElement route)
        {
            bool fallback=route.GetProperty("fallback").GetBoolean();
            hint.Text=fallback?"Chosen device unavailable. Using Windows default until it reconnects.":"Voice plays through "+(speechOutputDevice.Length==0?"Windows default.":speechOutputLabel+".");
            hint.Foreground=new SolidColorBrush(fallback?Color.FromRgb(230,199,161):Color.FromRgb(171,196,181));
        }
        async Task Refresh()
        {
            if(closed)return;if(refreshing){refreshAgain=true;return;}
            refreshing=true;refresh.IsEnabled=false;
            try
            {
                if(companion==null)throw new InvalidOperationException("Show the companion, then refresh devices.");
                var result=await companion.GetAudioOutputs();
                var choices=new List<SpeechOutputChoice>{new("","Windows default")};
                foreach(var device in result.GetProperty("devices").EnumerateArray())choices.Add(new(device.GetProperty("id").GetString()!,device.GetProperty("label").GetString()!));
                if(speechOutputDevice.Length>0&&!choices.Any(c=>c.Id==speechOutputDevice))choices.Add(new(speechOutputDevice,(speechOutputLabel.Length>0?speechOutputLabel:"Saved output device")+" (not connected)"));
                if(closed)return;
                syncing=true;try{output.ItemsSource=choices;output.SelectedValue=speechOutputDevice;output.ToolTip=speechOutputDevice.Length==0?"Follows the default playback device in Windows.":speechOutputLabel;}finally{syncing=false;}
                ShowRoute(await companion.SetAudioOutput(speechOutputDevice,speechOutputLabel));
                if(!result.GetProperty("supported").GetBoolean())hint.Text="This WebView2 version supports Windows default only. Update WebView2 to choose other speakers.";
            }
            catch(Exception ex){if(!closed)hint.Text=ex.Message;}
            finally{refreshing=false;refresh.IsEnabled=true;if(refreshAgain){refreshAgain=false;_ = Refresh();}}
        }
        output.SelectionChanged+=async(_,_)=>
        {
            if(syncing||closed||output.SelectedItem is not SpeechOutputChoice choice)return;
            speechOutputDevice=choice.Id;speechOutputLabel=choice.Id.Length==0?"":choice.Label.Replace(" (not connected)","");SaveVoicePreferences();
            output.ToolTip=choice.Label;
            try{if(companion!=null)ShowRoute(await companion.SetAudioOutput(speechOutputDevice,speechOutputLabel));}
            catch(Exception ex){hint.Text=ex.Message;}
        };
        refresh.Click+=async(_,_)=>await Refresh();
        test.Click+=async(_,_)=>
        {
            test.IsEnabled=false;
            try{if(companion==null)throw new InvalidOperationException("Show the companion before testing speakers.");ShowRoute(await companion.TestAudioOutput(smokeTest));}
            catch(Exception ex){hint.Text=ex.Message;}
            finally{test.IsEnabled=true;}
        };
        Action changed=()=>_ = Refresh();
        if(companion!=null)companion.AudioOutputsChanged+=changed;
        audioOutputPanelCleanup=()=>{closed=true;if(companion!=null)companion.AudioOutputsChanged-=changed;};
        output.Loaded+=async(_,_)=>await Refresh();
    }
}
