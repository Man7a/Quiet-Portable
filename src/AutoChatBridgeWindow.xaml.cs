using System.Windows;
using System.Windows.Controls;

namespace QuietGPT;
public partial class AutoChatBridgeWindow : Window
{
    private readonly AutoChatBridge bridge;
    public AutoChatBridgeWindow(AutoChatBridge bridge)
    {
        this.bridge=bridge;InitializeComponent();bridge.Changed+=Update;
        Closed+=(_,_)=>bridge.Changed-=Update;Loaded+=async(_,_)=>await Refresh();Update();
    }
    private void Update()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.Invoke(Update);return;}
        var selected=Connections.SelectedItem;Connections.ItemsSource=null;Connections.ItemsSource=bridge.Bindings;Connections.SelectedItem=selected;
        Requests.ItemsSource=bridge.Jobs.GroupBy(j=>j.CollaborationId.Length>0?j.CollaborationId:j.Id).Select(g=>g.OrderByDescending(j=>j.Created).First()).OrderByDescending(j=>j.Created).ToList();StatusText.Text=bridge.Status;
    }
    private async Task Refresh()
    {
        try{
            StatusText.Text="Connecting to Codex...";await bridge.RefreshChats();
            RayChats.ItemsSource=bridge.Chats.Where(c=>c.Kind=="chatgpt").ToList();MiraChats.ItemsSource=bridge.Chats.Where(c=>c.Kind=="codex").ToList();
            RayChats.SelectedItem=bridge.Chats.FirstOrDefault(c=>c.Id==RayMiraStore.RayId);MiraChats.SelectedItem=bridge.Chats.FirstOrDefault(c=>c.Id==RayMiraStore.CodexId);
            Update();
        }catch(Exception ex){StatusText.Text="Could not connect: "+ex.Message;}
    }
    private async void RefreshClick(object sender,RoutedEventArgs e)=>await Refresh();
    private void AddClick(object sender,RoutedEventArgs e)
    {
        try{if(RayChats.SelectedItem is not AutoChatBridge.Chat ray||MiraChats.SelectedItem is not AutoChatBridge.Chat mira)throw new InvalidOperationException("Select both conversations first.");bridge.Add(ray,mira);bridge.SetEnabled(bridge.Bindings.Last(),true);Update();}
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private void ToggleClick(object sender,RoutedEventArgs e)
    {
        if(sender is CheckBox check&&check.DataContext is AutoChatBridge.Binding b){try{bridge.SetEnabled(b,check.IsChecked==true);Update();}catch(Exception ex){StatusText.Text=ex.Message;}}
    }
    private void StopClick(object sender,RoutedEventArgs e){if(sender is Button button&&button.DataContext is AutoChatBridge.Job job){bridge.Stop(job);{var binding=bridge.Bindings.FirstOrDefault(b=>b.Id==job.BindingId);if(binding!=null)bridge.SetEnabled(binding,false);}Update();}}
    private void RetryClick(object sender,RoutedEventArgs e)
    {
        if(sender is not Button button||button.DataContext is not AutoChatBridge.Job job)return;
        if(MessageBox.Show(this,"Check the paired Codex task first. Retry only if Mira did not receive this request. Sending twice could repeat the work. Have you checked?","Retry uncertain delivery",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        try{bridge.RetryChecked(job);Update();}catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private void RemoveClick(object sender,RoutedEventArgs e){try{if(Connections.SelectedItem is AutoChatBridge.Binding b)bridge.Remove(b);Update();}catch(Exception ex){StatusText.Text=ex.Message;}}
}
