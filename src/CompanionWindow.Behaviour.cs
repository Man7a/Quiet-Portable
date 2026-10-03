using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace QuietGPT;

public partial class CompanionWindow
{
    private Window? quietWindow, behaviourWindow;
    private readonly DispatcherTimer contextTimer=new() { Interval=TimeSpan.FromMilliseconds(200) };
    private Popup? indicator;
    private TextBlock? speechCaption;
    private StackPanel? thinkingDots;
    private string spokenText="";
    private bool quietForeground=true, placing, closed, dragging;
    internal string IndicatorKind { get; private set; }="none";
    internal bool Attached => settings.Attached;
    internal FrameworkElement? IndicatorSurface => indicator?.Child as FrameworkElement;

    private void InitializeBehaviour()
    {
        MouseEnter+=(_,_)=>{HoverControls.Opacity=0;ResizeHandle.Opacity=0;};
        MouseLeave+=(_,_)=>{HoverControls.Opacity=0;ResizeHandle.Opacity=0;};
        contextTimer.Tick+=(_,_)=>UpdateContext();
        IsVisibleChanged+=(_,_)=>RefreshIndicator();
        LocationChanged+=(_,_)=>PositionIndicator();
        Closed+=(_,_)=>{closed=true;contextTimer.Stop();if(indicator!=null)indicator.IsOpen=false;behaviourWindow?.Close();};
    }

    public void ConnectToQuiet(Window window,bool startTimer=true)
    {
        quietWindow=window;
        window.LocationChanged+=(_,_)=>{if(settings.Attached)PlaceAttached();};
        window.SizeChanged+=(_,_)=>{if(settings.Attached)PlaceAttached();};
        window.StateChanged+=(_,_)=>UpdateContext();
        if(startTimer)contextTimer.Start();
        ApplyPlacementMode();
    }

    private void UpdateContext()
    {
        if(closed||quietWindow==null)return;
        var current=GetForegroundWindow();
        var main=new WindowInteropHelper(quietWindow).Handle;
        bool foreground=current==main;
        for(int i=0;!foreground&&current!=IntPtr.Zero&&i<8;i++) { current=GetWindow(current,4);foreground=current==main; }
        ApplyContext(foreground);
    }

    internal void ApplyContext(bool foreground)
    {
        if(closed||quietWindow==null)return;
        quietForeground=foreground;
        bool show=settings.Visible && (settings.Attached ? quietWindow.IsVisible&&quietWindow.WindowState!=WindowState.Minimized : settings.DesktopAlwaysVisible||foreground);
        bool wasVisible=IsVisible;
        if(show&&!IsVisible)Show();else if(!show&&IsVisible)Hide();
        if(wasVisible!=IsVisible)VisibilityPreferenceChanged?.Invoke();
        if(show&&settings.Attached)PlaceAttached();
        RefreshIndicator();
    }

    internal void SetPlacement(bool attached)
    {
        if(settings.Attached==attached)return;
        if(!settings.Attached){settings.Left=Left;settings.Top=Top;}
        settings.Attached=attached;
        ApplyPlacementMode();Save();
    }

    private void ApplyPlacementMode()
    {
        if(quietWindow==null)return;
        Owner=settings.Attached?quietWindow:null;
        Topmost=!settings.Attached&&settings.Pinned;
        PinButton.Content=Topmost?"◆":"◇";
        PinButton.IsEnabled=!settings.Attached;
        HoverControls.Visibility=Visibility.Collapsed;
        ResizeHandle.Visibility=Visibility.Collapsed;
        if(settings.Attached)PlaceAttached();
        else if(settings.Positioned&&double.IsFinite(settings.Left)&&double.IsFinite(settings.Top)){Left=settings.Left;Top=settings.Top;KeepOnScreen();}
        UpdateContext();
    }

    private Rect QuietBounds()
    {
        if(quietWindow==null||PresentationSource.FromVisual(quietWindow)==null)return WorkArea();
        var transform=PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice??Matrix.Identity;
        var a=transform.Transform(quietWindow.PointToScreen(new Point(8,60)));
        var b=transform.Transform(quietWindow.PointToScreen(new Point(Math.Max(9,quietWindow.ActualWidth-8),Math.Max(61,quietWindow.ActualHeight-(settings.Backdrop?8:1)))));
        return new Rect(a,b);
    }

    private void PlaceAttached()
    {
        if(placing||dragging||quietWindow==null||!settings.Attached||quietWindow.WindowState==WindowState.Minimized)return;
        placing=true;
        try
        {
            var r=QuietBounds();
            Left=settings.DockCorner.EndsWith("right")?Math.Max(r.Left,r.Right-Width):r.Left;
            Top=settings.Backdrop&&settings.DockCorner.StartsWith("Top")?r.Top:Math.Max(r.Top,r.Bottom-Height);
        }
        finally{placing=false;}
    }

    private void BringQuietForward()
    {
        if(quietWindow==null)return;
        if(quietWindow.WindowState==WindowState.Minimized)quietWindow.WindowState=WindowState.Normal;
        quietWindow.Show();quietWindow.Activate();
    }

    public void SetSpokenText(string text){spokenText=text;RefreshIndicator();}
    private void RefreshIndicator()
    {
        if(!IsInitialized||closed)return;
        Expanded.Background=settings.Backdrop?new SolidColorBrush(Color.FromArgb(230,28,37,32)):Brushes.Transparent;
        AvatarLayout.Margin=new Thickness(settings.Backdrop?10:0);
        bool speech=IsVisible&&!Collapsed&&!settings.Attached&&!quietForeground&&settings.SpeechBubbles&&spokenText.Length>0;
        bool thinking=IsVisible&&!Collapsed&&settings.ThinkingIndicator&&spokenText.Length==0&&Activity is "thinking" or "responding";
        IndicatorKind=speech?"speech":thinking?"thinking":"none";
        if(IndicatorKind=="none"){if(indicator!=null)indicator.IsOpen=false;return;}
        EnsureIndicator();
        speechCaption!.Text=speech?spokenText:"";
        speechCaption.Visibility=speech?Visibility.Visible:Visibility.Collapsed;
        thinkingDots!.Visibility=thinking?Visibility.Visible:Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(indicator!.Child,thinking?"Quiet is preparing a reply":spokenText);
        indicator.IsOpen=true;PositionIndicator();
    }

    private void EnsureIndicator()
    {
        if(indicator!=null)return;
        speechCaption=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(233,245,237)),FontSize=13,TextWrapping=TextWrapping.Wrap,MaxWidth=240,MaxHeight=240};
        thinkingDots=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(1,5,1,5)};
        for(int i=0;i<3;i++)
        {
            var dot=new Ellipse{Width=6,Height=6,Fill=new SolidColorBrush(Color.FromRgb(174,221,193)),Margin=new Thickness(4,0,4,0),RenderTransform=new TranslateTransform()};
            var motion=new DoubleAnimation(0,-4,TimeSpan.FromSeconds(.42)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,BeginTime=TimeSpan.FromSeconds(i*.16),EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}};
            dot.RenderTransform.BeginAnimation(TranslateTransform.YProperty,motion);
            dot.BeginAnimation(OpacityProperty,new DoubleAnimation(.4,1,TimeSpan.FromSeconds(.42)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever,BeginTime=TimeSpan.FromSeconds(i*.16)});
            thinkingDots.Children.Add(dot);
        }
        var content=new StackPanel();content.Children.Add(speechCaption);content.Children.Add(thinkingDots);
        var surface=new Border{Background=new SolidColorBrush(Color.FromArgb(245,32,49,39)),BorderBrush=new SolidColorBrush(Color.FromRgb(104,141,119)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(15,15,15,4),Padding=new Thickness(12,10,12,10),Child=content};
        indicator=new Popup{Child=surface,PlacementTarget=this,Placement=PlacementMode.Relative,StaysOpen=true,AllowsTransparency=true,IsHitTestVisible=false};
        indicator.Opened+=(_,_)=>SyncIndicatorOwner();
    }

    private void SyncIndicatorOwner()
    {
        if(indicator?.Child is not Visual visual || PresentationSource.FromVisual(visual) is not HwndSource source)return;
        // WPF popups default to topmost. An attached indicator belongs in the
        // same owned-window chain as the companion, underneath other apps.
        SetIndicatorOwner(source.Handle,-8,new WindowInteropHelper(this).Handle);
        bool isTop=(GetIndicatorStyle(source.Handle,-20).ToInt64()&8)!=0;
        bool desired=!settings.Attached&&Topmost;
        if(isTop!=desired)SetIndicatorPosition(source.Handle,desired?new IntPtr(-1):new IntPtr(-2),0,0,0,0,0x13);
    }
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetIndicatorOwner(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetIndicatorStyle(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowPos")] private static extern bool SetIndicatorPosition(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);

    private void PositionIndicator()
    {
        if(indicator?.IsOpen!=true)return;
        indicator.Child.Measure(new Size(270,300));var size=indicator.Child.DesiredSize;
        var area=WorkArea();double x=IndicatorKind=="thinking"?Width*.32-size.Width-5:(Left>=area.Left+size.Width+6?-size.Width-6:Width+6);
        if(indicator.Child is Border border)border.CornerRadius=IndicatorKind=="thinking"?new CornerRadius(15,15,4,15):new CornerRadius(15,15,15,4);
        x=Math.Clamp(Left+x,area.Left,Math.Max(area.Left,area.Right-size.Width))-Left;
        double y=Math.Clamp(Top+Height*(IndicatorKind=="thinking"?.17:.25),area.Top,Math.Max(area.Top,area.Bottom-size.Height))-Top;
        indicator.HorizontalOffset=x;indicator.VerticalOffset=y;
        SyncIndicatorOwner();
    }

    private void BehaviourSettingsClick(object sender,RoutedEventArgs e){if(quietWindow!=null)ShowBehaviourSettings(quietWindow);}
    internal ContextMenu? CompanionMenu {get;private set;}
    private void ShowCompanionMenu()
    {
        var menu=new ContextMenu{Background=new SolidColorBrush(Color.FromRgb(28,42,34)),BorderBrush=new SolidColorBrush(Color.FromRgb(77,105,87)),Padding=new Thickness(6),MinWidth=210};
        CompanionMenu=menu;
        void Add(string title,Action action){var item=new MenuItem{Header=title};item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Add("Companion settings…",()=>{if(quietWindow!=null)ShowBehaviourSettings(quietWindow);});
        Add("Style & particles…",()=>{if(quietWindow!=null)ShowBehaviourSettings(quietWindow,1);});
        menu.Items.Add(new Separator());
        Add(settings.Attached?"Move to desktop":"Attach to Quiet",()=>SetPlacement(!settings.Attached));
        Add("Settle at bottom left",()=>Snap("Bottom left"));
        Add("Settle at bottom right",()=>Snap("Bottom right"));
        if(!settings.Attached)Add("Bring Quiet forward",BringQuietForward);
        menu.PlacementTarget=this;menu.Placement=PlacementMode.MousePoint;menu.IsOpen=true;
    }
    public void ShowBehaviourSettings(Window owner,int selectedTab=0)
    {
        if(behaviourWindow!=null){behaviourWindow.Activate();return;}
        var panel=new StackPanel{Margin=new Thickness(20)};
        panel.Children.Add(new TextBlock{Text="Companion",FontSize=22,Margin=new Thickness(0,0,0,14)});
        var placement=new ComboBox{ItemsSource=new[]{"Attached to Quiet","Desktop companion"},SelectedIndex=settings.Attached?0:1,Margin=new Thickness(0,0,0,10),Padding=new Thickness(8)};
        panel.Children.Add(new TextBlock{Text="Placement",Margin=new Thickness(0,0,0,5)});panel.Children.Add(placement);
        var desktopOptions=new StackPanel();panel.Children.Add(desktopOptions);
        desktopOptions.Children.Add(new TextBlock{Text="DESKTOP ONLY",FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(163,192,174)),Margin=new Thickness(0,8,0,5)});
        CheckBox Check(string label,bool value,Action<bool> change,Panel parent)
        {
            var box=new CheckBox{Content=label,IsChecked=value,Margin=new Thickness(0,7,0,7),Foreground=Brushes.WhiteSmoke};
            box.Click+=(_,_)=>{change(box.IsChecked==true);Save();UpdateContext();};parent.Children.Add(box);return box;
        }
        Check("Stay visible when Quiet is in the background",settings.DesktopAlwaysVisible,v=>settings.DesktopAlwaysVisible=v,desktopOptions);
        Check("Stay above other apps",settings.Pinned,v=>{settings.Pinned=v;Topmost=v;PinButton.Content=v?"◆":"◇";},desktopOptions);
        Check("Speech bubbles while Quiet is in the background",settings.SpeechBubbles,v=>settings.SpeechBubbles=v,desktopOptions);
        Check("Snap near the bottom (all edges with backdrop)",settings.SnapEdges,v=>settings.SnapEdges=v,desktopOptions);
        Check("Double-click her to bring Quiet forward",settings.DoubleClickOpensQuiet,v=>settings.DoubleClickOpensQuiet=v,desktopOptions);
        panel.Children.Add(new TextBlock{Text="BOTH PLACEMENTS",FontSize=11,Foreground=new SolidColorBrush(Color.FromRgb(163,192,174)),Margin=new Thickness(0,16,0,5)});
        Check("Animated thinking indicator",settings.ThinkingIndicator,v=>settings.ThinkingIndicator=v,panel);
        var backdrop=Check("Show a soft backdrop",settings.Backdrop,v=>settings.Backdrop=v,panel);
        panel.Children.Add(new TextBlock{Text="Corner",Margin=new Thickness(0,10,0,5)});
        var corner=new ComboBox{ItemsSource=settings.Backdrop?new[]{"Top left","Top right","Bottom left","Bottom right"}:new[]{"Bottom left","Bottom right"},SelectedItem=settings.Backdrop?settings.DockCorner:(settings.DockCorner.EndsWith("left")?"Bottom left":"Bottom right"),Padding=new Thickness(8)};
        corner.SelectionChanged+=(_,_)=>{if(corner.SelectedItem is string value)Snap(value);};panel.Children.Add(corner);
        backdrop.Click+=(_,_)=>{corner.ItemsSource=settings.Backdrop?new[]{"Top left","Top right","Bottom left","Bottom right"}:new[]{"Bottom left","Bottom right"};corner.SelectedItem=settings.Backdrop?settings.DockCorner:(settings.DockCorner.EndsWith("left")?"Bottom left":"Bottom right");};
        placement.SelectionChanged+=(_,_)=>{SetPlacement(placement.SelectedIndex==0);desktopOptions.Visibility=settings.Attached?Visibility.Collapsed:Visibility.Visible;};
        desktopOptions.Visibility=settings.Attached?Visibility.Collapsed:Visibility.Visible;
        var actions=new WrapPanel{Margin=new Thickness(0,14,0,0)};
        void Button(string label,Action action){var b=new Button{Content=label,Margin=new Thickness(0,0,7,7)};b.Click+=(_,_)=>action();actions.Children.Add(b);}
        Button("Bring her into view",()=>{settings.Visible=true;if(settings.Attached){BringQuietForward();PlaceAttached();}else Snap("Bottom right");Show();Save();UpdateContext();});
        Button("Done",()=>behaviourWindow?.Close());panel.Children.Add(actions);
        panel.Children.Add(new TextBlock{Text="Attached mode stays with Quiet, even in the background, and hides when Quiet is minimized. Desktop mode can show speech bubbles while Quiet is in the background.",TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(174,198,182)),FontSize=12,Margin=new Thickness(0,8,0,0)});
        panel.Children.Add(new TextBlock{Text="Size",Margin=new Thickness(0,12,0,5)});
        var size=new Slider{Minimum=210,Maximum=440,Value=Collapsed?settings.Width:Width,TickFrequency=5,IsSnapToTickEnabled=true};
        size.ValueChanged+=(_,_)=>{if(Collapsed)SetCollapsed(false);var ratio=Height/Width;Width=size.Value;Height=Math.Clamp(Width*ratio,250,520);PlaceAttached();KeepOnScreen();Save();};panel.Children.Add(size);
        var tabs=new TabControl{Background=new SolidColorBrush(Color.FromRgb(23,31,27)),Foreground=Brushes.WhiteSmoke,BorderThickness=new Thickness(0)};
        tabs.Items.Add(new TabItem{Header="General",Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        var styles=new StackPanel{Margin=new Thickness(20)};
        var styleTab=new TabItem{Header="Style",Content=new ScrollViewer{Content=styles,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};tabs.Items.Add(styleTab);
        bool styleLoaded=false;tabs.SelectionChanged+=async(_,_)=>{if(styleTab.IsSelected&&!styleLoaded){styleLoaded=true;await PopulateStyleTab(styles);}};
        var help=new StackPanel{Margin=new Thickness(20)};
        help.Children.Add(new TextBlock{Text="Get to know her",FontSize=22,Margin=new Thickness(0,0,0,16)});
        void Tip(string title,string text)
        {
            var card=new StackPanel{Margin=new Thickness(16,12,16,14)};
            card.Children.Add(new TextBlock{Text=title,FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,5)});
            card.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,LineHeight=20,Foreground=new SolidColorBrush(Color.FromRgb(174,198,182))});
            help.Children.Add(new Border{Child=card,Background=new SolidColorBrush(Color.FromRgb(30,44,35)),CornerRadius=new CornerRadius(9),Margin=new Thickness(0,0,0,10)});
        }
        Tip("Move & resize","Hold either shoulder to move her. Use Size in General to resize her. Without a backdrop, she settles along the bottom; with a backdrop, she can use any corner.");
        Tip("Read a reply","Click her lips to read the latest completed reply. Click again to stop. Open Read aloud for voice options, Pause and speech speed. Automatic reading follows new replies and leaves old chat history silent.");
        Tip("Eyes & expressions","Click an eye for a brief blink and brow reaction. Rub her head for blush and expressions.");
        Tip("Make her your own","Open Style to change her look, motion and particles. Motion tests run without closing the controls. Changes save as you adjust them.");
        Tip("Keep Quiet close","Right-click her to open settings or change placement. In Desktop mode, double-click her to bring Quiet forward when that option is enabled.");
        tabs.Items.Add(new TabItem{Header="Interactions",Content=new ScrollViewer{Content=help,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        tabs.SelectedIndex=selectedTab;
        behaviourWindow=new Window{Title="Quiet settings · Companion",Owner=owner,Content=tabs,Width=540,Height=710,MinHeight=480,MaxHeight=900,Background=new SolidColorBrush(Color.FromRgb(23,31,27)),Foreground=Brushes.WhiteSmoke,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        QuietPanel.Apply(behaviourWindow);
        behaviourWindow.Closed+=(_,_)=>behaviourWindow=null;behaviourWindow.Show();
    }

    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern IntPtr GetWindow(IntPtr window,uint command);
}



