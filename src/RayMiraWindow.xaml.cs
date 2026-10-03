using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuietGPT;

public partial class RayMiraWindow : Window
{
    private readonly RayMiraStore store;
    private readonly Func<Task<string>> importReply;
    private readonly DispatcherTimer timer = new() {Interval=TimeSpan.FromSeconds(2)};
    private readonly DispatcherTimer saveTimer = new() {Interval=TimeSpan.FromMilliseconds(650)};
    private bool ready;
    internal RayMiraWindow(RayMiraStore store, Func<Task<string>> importReply)
    {
        this.store=store;this.importReply=importReply;InitializeComponent();
        EnabledBox.IsChecked=store.Settings.Enabled;RequestBox.Text=store.Settings.Draft;ready=true;
        timer.Tick+=(_,_)=>RefreshState();
        saveTimer.Tick+=(_,_)=>{saveTimer.Stop();Save();};
        IsVisibleChanged+=(_,_)=>UpdateTimer();
        Closed+=(_,_)=>{timer.Stop();saveTimer.Stop();Save();};
        RefreshState();
    }
    private bool Save()
    {
        try {store.Save(EnabledBox.IsChecked==true,RequestBox.Text);return true;}
        catch(Exception ex){Feedback.Text="Could not save: "+ex.Message;return false;}
    }
    private void DraftChanged(object sender,TextChangedEventArgs e) {if(ready){saveTimer.Stop();saveTimer.Start();}}
    private void EnabledClick(object sender,RoutedEventArgs e){Save();UpdateTimer();RefreshState();}
    private void UpdateTimer(){if(IsVisible&&store.Settings.Enabled)timer.Start();else timer.Stop();}
    private void RefreshClick(object sender,RoutedEventArgs e)=>RefreshState();
    internal void RefreshState()
    {
        try {
            var current=store.Current();var result=store.ReadResult();bool on=store.Settings.Enabled;
            RequestBox.IsReadOnly=current!=null;
            LoadReplyButton.IsEnabled=on&&current==null;ReviewButton.IsEnabled=on&&current==null;
            ReturnButton.IsEnabled=on&&result!=null&&!string.IsNullOrWhiteSpace(result.Text);
            ReopenButton.IsEnabled=on&&current!=null;CloseJobButton.IsEnabled=current!=null;
            StateText.Text=!on?"Connection off":current==null?"Ready · Prepare a request":result?.Status switch {
                "working"=>"Mira is working · Update received", "needs_input"=>"Mira needs your input", "completed"=>"Result ready for review", "failed"=>"Mira reported a problem", _=>"Handoff prepared · Paste and send it in Codex"};
            if(result!=null)ResultBox.Text=result.Text;
            else ResultBox.Text=current==null?"No handoff yet.":"No update received yet. Copying a handoff does not send it. Paste it in the paired Codex task and press Send.";
        }catch(Exception ex){Feedback.Text="Could not read handoff: "+ex.Message;ReviewButton.IsEnabled=false;ReturnButton.IsEnabled=false;ResultBox.Text="Update unavailable. The saved result could not be verified.";}
    }
    private async void LoadReplyClick(object sender,RoutedEventArgs e)
    {
        if(!store.Settings.Enabled||store.Current()!=null)return;
        string before=RequestBox.Text;LoadReplyButton.IsEnabled=false;
        try {
            string text=await importReply();
            if(!IsVisible||!store.Settings.Enabled||store.Current()!=null||RequestBox.Text!=before)return;
            if(!string.IsNullOrWhiteSpace(before) && MessageBox.Show(this,"Replace the request draft with Ray’s latest reply?","Replace draft",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            RequestBox.Text=text;Save();Feedback.Text="Imported from Workbench. Edit this down to the task you want Mira to handle.";
        }catch(Exception ex){Feedback.Text=ex.Message;}
        finally{RefreshState();}
    }
    private void ReviewClick(object sender,RoutedEventArgs e)
    {
        try {
            if(!Save())return;
            var request=store.Prepare(RequestBox.Text);
            Review("Review handoff to Mira",request.Prompt,"Copy & open Codex",()=>{
                store.Approve(request);Clipboard.SetText(request.Prompt);
                Feedback.Text="Copied. In Codex, paste into Quiet GPT browser PROJECT and press Send. Nothing has been sent yet.";
                OpenTask();Tabs.SelectedIndex=1;RefreshState();
            });
        }catch(Exception ex){Feedback.Text=ex.Message;}
    }
    private void ReopenClick(object sender,RoutedEventArgs e)
    {
        try {if(store.Current() is {} r)Review("Approved handoff",r.Prompt,"Copy & open Codex",()=>{Clipboard.SetText(r.Prompt);OpenTask();Feedback.Text="Copied the same request. If you already sent it, check Codex before sending again.";});}
        catch(Exception ex){Feedback.Text=ex.Message;}
    }
    private void ReturnClick(object sender,RoutedEventArgs e)
    {
        try {if(store.ReadResult() is {} r)Review("Review result for Ray",store.ReturnText(r),"Copy for Workbench",()=>{Clipboard.SetText(store.ReturnText(r));Feedback.Text="Copied. Open Workbench, paste this result and send when ready. Nothing has been sent automatically.";});}
        catch(Exception ex){Feedback.Text=ex.Message;}
    }
    private void OpenTaskClick(object sender,RoutedEventArgs e)=>OpenTask();
    private void OpenTask()
    {
        try {Process.Start(new ProcessStartInfo("codex://threads/"+RayMiraStore.CodexId){UseShellExecute=true});}
        catch {Feedback.Text="Copied if requested. Open Codex manually and choose Quiet GPT browser PROJECT.";}
    }
    private void CloseJobClick(object sender,RoutedEventArgs e)
    {
        if(MessageBox.Show(this,"Close this handoff in Quiet? This keeps its saved files and does not cancel work in Codex. Check Codex first if you already sent it.","Close handoff",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        try{store.CloseHandoff();RefreshState();Feedback.Text="Handoff closed. Its files are retained.";}catch(Exception ex){Feedback.Text=ex.Message;}
    }
    private void Review(string title,string text,string buttonText,Action approved)
    {
        var window=new Window {Owner=this,Title=title,Width=690,Height=640,MinWidth=500,MinHeight=400,Background=Background,Foreground=Foreground,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var grid=new Grid {Margin=new Thickness(20)};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(new TextBlock {Text="This is the exact text that will be copied. Nothing is submitted automatically.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
        var box=new TextBox {Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(16,24,19)),Foreground=Foreground,Padding=new Thickness(12)};Grid.SetRow(box,1);grid.Children.Add(box);
        var row=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};Grid.SetRow(row,2);
        var cancel=new Button {Content="Back",Margin=new Thickness(0,0,10,0)};cancel.Click+=(_,_)=>window.Close();row.Children.Add(cancel);
        var ok=new Button {Content=buttonText,Background=new SolidColorBrush(Color.FromRgb(54,92,69))};ok.Click+=(_,_)=>{try{approved();window.Close();}catch(Exception ex){MessageBox.Show(window,ex.Message,"Could not prepare handoff");}};row.Children.Add(ok);grid.Children.Add(row);window.Content=grid;window.ShowDialog();
    }
}
