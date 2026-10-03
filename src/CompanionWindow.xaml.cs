using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuietGPT;

public partial class CompanionWindow : Window
{
    public async Task PlaySpeech(JsonElement bundle, CancellationToken token, string? mood = null) { try { await RiggedAvatar.PlaySpeech(bundle, token, mood); } finally { SetSpokenText(""); } }
    public void StopSpeech() { SetSpokenText(""); RiggedAvatar.StopSpeech(); }
    public void SetSpeechPaused(bool value) => RiggedAvatar.SetSpeechPaused(value);
    public event Action? AudioOutputsChanged { add=>RiggedAvatar.AudioOutputsChanged+=value;remove=>RiggedAvatar.AudioOutputsChanged-=value; }
    public Task<JsonElement> GetAudioOutputs()=>RiggedAvatar.GetAudioOutputs();
    public Task<JsonElement> SetAudioOutput(string deviceId,string label,CancellationToken token=default)=>RiggedAvatar.SetAudioOutput(deviceId,label,token);
    public Task<JsonElement> TestAudioOutput(bool silent=false)=>RiggedAvatar.TestAudioOutput(silent);
    public void SetSpeechRate(double value) => RiggedAvatar.SetSpeechRate(value);
    public void ReactToSpeechStop() => RiggedAvatar.ReactToSpeechStop();
    private readonly string settingsPath;
    private readonly DispatcherTimer gazeTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    public bool IsEyeTrackingRunning => gazeTimer.IsEnabled;
    private readonly Dictionary<string, BitmapImage> pictures = new();
    private CompanionSettings settings = new();
    private bool rigFailed;
    public bool IsRiggedAvatarReady => RiggedAvatar.Ready;
    public bool IsRiggedAvatarRunning => RiggedAvatar.Running;
    public string? RiggedAvatarError => RiggedAvatar.LastError;
    public Task<string> InspectRiggedAvatarAsync(string script) => RiggedAvatar.InspectAsync(script);
    public string Activity { get; private set; } = "idle";
    public bool Collapsed => Bubble.Visibility == Visibility.Visible;
    public bool WantedVisible => settings.Visible;
    public event Action? VisibilityPreferenceChanged;
    public event Action? ReadReplyRequested;
    public void HideCompanion() => HideClick(this,new());

    public CompanionWindow(string dataRoot)
    {
        settingsPath = Path.Combine(dataRoot, "companion.json");
        InitializeComponent();
        InitializeBehaviour();
        RiggedAvatar.Configure(dataRoot);
        RiggedAvatar.ReadReplyRequested += () => ReadReplyRequested?.Invoke();
        RiggedAvatar.OpenQuietRequested += () => { if(!settings.Attached&&settings.DoubleClickOpensQuiet)BringQuietForward(); };
        RiggedAvatar.SettingsRequested += () => { if(quietWindow!=null)ShowBehaviourSettings(quietWindow); };
        RiggedAvatar.MenuRequested += ShowCompanionMenu;
        RiggedAvatar.ShoulderDragRequested += ShoulderDrag;
        RiggedAvatar.AvatarReady += () => { RiggedAvatar.MoveWindow(Left, Top); SetActivity(Activity); UpdateSpatialPosition(); };
        RiggedAvatar.AvatarFailed += () => { rigFailed = true; SetActivity(Activity); };
        LocationChanged += (_, _) => { if (IsVisible && !Collapsed) RiggedAvatar.MoveWindow(Left, Top); UpdateSpatialPosition(); };
        SizeChanged += (_, _) => UpdateSpatialPosition();
        try
        {
            if(File.Exists(settingsPath))
            {
                var saved=File.ReadAllText(settingsPath);
                settings=JsonSerializer.Deserialize<CompanionSettings>(saved)??new();
                using var old=JsonDocument.Parse(saved);
                // Retired video selections become the logo; other saved modes stay intact.
                if(!settings.RiggedMode&&old.RootElement.TryGetProperty("VideoMode",out var video)&&video.ValueKind==JsonValueKind.True)
                {settings.LayeredEyes=false;settings.ImageFolder=null;}
            }
        }
        catch { }

        Width = Math.Clamp(settings.Width, 210, 440);
        Height = Math.Clamp(settings.Height, 250, 520);
        Topmost = settings.Pinned;
        PinButton.Content = Topmost ? "◆" : "◇";
        if (settings.Positioned && double.IsFinite(settings.Left) && double.IsFinite(settings.Top)) { Left = settings.Left; Top = settings.Top; }
        else { Left = SystemParameters.WorkArea.Right - Width - 24; Top = SystemParameters.WorkArea.Bottom - Height - 24; }
        if(!settings.Positioned){settings.Left=Left;settings.Top=Top;}
        SourceInitialized += (_, _) => { KeepOnScreen(); if (settings.Collapsed) SetCollapsed(true); };
        IsVisibleChanged += (_, _) => UpdateAvatarRunning();
        Closed += (_, _) => { gazeTimer.Stop(); RiggedAvatar.CloseAvatar(); Save(); };
        DefaultAvatar.MouseRightButtonUp += (_, e) => { ShowCompanionMenu();e.Handled=true; };
        gazeTimer.Tick += (_, _) => UpdateGaze();
        EyeTrackingAvatar.MouseRightButtonUp += (_, e) =>
        {
            var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(28, 37, 32)) };
            var tracking = new MenuItem { Header = settings.FollowMouse ? "Pause eye tracking" : "Follow mouse" };
            tracking.Click += (_, _) => { settings.FollowMouse = !settings.FollowMouse; if (!settings.FollowMouse) EyeTrackingAvatar.SetGaze(0, 0); UpdateAvatarRunning(); Save(); };
            menu.Items.Add(tracking); menu.PlacementTarget = EyeTrackingAvatar; menu.IsOpen = true; e.Handled = true;
        };
        LoadPictures();
        SetActivity("idle");
    }

    public void SetActivity(string activity)
    {
        Activity = activity;
        ActivityLabel.Text = activity switch { "thinking" => "Thinking…", "responding" => "Writing a reply…", "done" => "Reply ready", "download" => "File saved", "error" => "Needs attention", "unknown" => "Here with you", _ => "Here with you" };
        BubbleFace.Text = activity switch { "thinking" => "···", "responding" => "✎", "done" or "download" => "✓", "error" => "!", _ => "•ᴗ•" };
        CustomAvatar.Source = pictures.GetValueOrDefault(activity) ?? pictures.GetValueOrDefault("idle");
        bool useRig = settings.RiggedMode && !rigFailed;
        RiggedAvatar.Visibility = useRig ? Visibility.Visible : Visibility.Collapsed;
        RiggedAvatar.SetActivity(activity);
        bool useEyes = !settings.RiggedMode && settings.LayeredEyes;
        bool usePicture = !settings.RiggedMode && !useEyes && CustomAvatar.Source is not null;
        EyeTrackingAvatar.Visibility = useEyes ? Visibility.Visible : Visibility.Collapsed;
        CustomAvatar.Visibility = usePicture ? Visibility.Visible : Visibility.Collapsed;
        DefaultAvatar.Visibility = !useRig && !useEyes && !usePicture ? Visibility.Visible : Visibility.Collapsed;
        UpdateAvatarRunning();
        RefreshIndicator();
        System.Windows.Automation.AutomationProperties.SetName(Bubble, ActivityLabel.Text + "; double-click to expand");
    }

    public void Reveal(bool activate = false)
    {
        settings.Visible = true;
        Show();
        if (activate) Activate();
        KeepOnScreen();
        Save();
        VisibilityPreferenceChanged?.Invoke();
        if(quietWindow!=null)UpdateContext();
    }

    private void HideClick(object sender, RoutedEventArgs e)
    {
        settings.Visible = false;
        Hide();
        Save();
        VisibilityPreferenceChanged?.Invoke();
    }

    public void ChoosePictures()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder with idle.png, thinking.png, responding.png, done.png, download.png and error.png" };
        if (dialog.ShowDialog(this) != true) return;
        settings.RiggedMode = false;
        settings.ImageFolder = dialog.FolderName;
        settings.LayeredEyes = false;
        LoadPictures();
        SetActivity(Activity);
        Save();
        if (pictures.Count == 0) MessageBox.Show(this, "No matching PNG files were found. Use idle.png, thinking.png, responding.png, done.png, download.png or error.png. Transparent images work best.", "Avatar images");
    }

    private void LoadPictures()
    {
        pictures.Clear();
        if (string.IsNullOrWhiteSpace(settings.ImageFolder)) return;
        foreach (string name in new[] { "idle", "thinking", "responding", "done", "download", "error", "unknown" })
        {
            try
            {
                string path = Path.Combine(settings.ImageFolder, name + ".png");
                if (!File.Exists(path)) continue;
                var picture = new BitmapImage();
                picture.BeginInit(); picture.CacheOption = BitmapCacheOption.OnLoad; picture.DecodePixelWidth = 800;
                picture.UriSource = new Uri(path); picture.EndInit(); picture.Freeze(); pictures[name] = picture;
            }
            catch { /* A missing or invalid pose falls back to idle or the built-in mascot. */ }
        }
    }

    private void PinClick(object sender, RoutedEventArgs e) { Topmost = !Topmost; PinButton.Content = Topmost ? "◆" : "◇"; Save(); }
    private void CollapseClick(object sender, RoutedEventArgs e) => SetCollapsed(true);
    public void SetCollapsed(bool collapsed)
    {
        if (collapsed == Collapsed) return;
        if (collapsed) { settings.Width = Width; settings.Height = Height; Width = Height = 60; }
        else { Width = Math.Clamp(settings.Width, 210, 440); Height = Math.Clamp(settings.Height, 250, 520); }
        Expanded.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Bubble.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        KeepOnScreen(); UpdateAvatarRunning(); Save();
    }
    public void UseRiggedAvatar(bool activate=true)
    {
        settings.RiggedMode = true; rigFailed = false; SetActivity(Activity); Reveal(activate); Save();
    }
    public void UseEyeTrackingAvatar()
    {
        settings.RiggedMode = false; settings.LayeredEyes = true; SetActivity(Activity); Reveal(true); Save();
    }
    internal void UseGhostAvatar()
    {
        settings.RiggedMode=false;settings.LayeredEyes=false;settings.ImageFolder=null;pictures.Clear();
        SetActivity(Activity);Reveal();Save();
    }
    private void UpdateAvatarRunning()
    {
        UpdateSpatialPosition();
        RiggedAvatar.SetRunning(IsVisible && !Collapsed && RiggedAvatar.Visibility == Visibility.Visible);
        if (IsVisible && !Collapsed && ((EyeTrackingAvatar.Visibility == Visibility.Visible && settings.FollowMouse) || RiggedAvatar.Visibility == Visibility.Visible)) gazeTimer.Start(); else gazeTimer.Stop();
    }
    private void UpdateGaze()
    {
        UpdateSpatialPosition();
        if (!GetCursorPos(out var cursor)) return;
        if (RiggedAvatar.Visibility == Visibility.Visible) { Point target = RiggedAvatar.PointFromScreen(new Point(cursor.X, cursor.Y)); RiggedAvatar.Aim(target.X, target.Y, settings.FollowMouse); return; }
        Point local = EyeTrackingAvatar.PointFromScreen(new Point(cursor.X, cursor.Y));
        EyeTrackingAvatar.AimAtLocalPoint(local);
    }
    private void BubbleClick(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) SetCollapsed(false); else DragCompanion(sender, e); }
    private void DragCompanion(object sender, MouseButtonEventArgs e)
    {
        if(e.ClickCount==2 && !settings.Attached && settings.DoubleClickOpensQuiet) { BringQuietForward(); return; }
        if (e.OriginalSource is DependencyObject source)
            for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
                if (node is ButtonBase or RiggedAvatarView) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        dragging=true;
        try { DragMove(); } catch (InvalidOperationException) { } finally {dragging=false;}
        if(quietWindow!=null && settings.Attached) { var dockArea=QuietBounds(); settings.DockCorner=(Top+Height/2<dockArea.Top+dockArea.Height/2?"Top ":"Bottom ")+(Left+Width/2<dockArea.Left+dockArea.Width/2?"left":"right"); PlaceAttached(); Save(); return; }
        Rect area = WorkArea();
        if(settings.SnapEdges) {
          if (Math.Abs(Left - area.Left) < 36) Left = area.Left + 12;
          if (Math.Abs(Left + Width - area.Right) < 36) Left = area.Right - Width - 12;
          if (Math.Abs(Top - area.Top) < 36) Top = area.Top + 12;
          if (Math.Abs(Top + Height - area.Bottom) < 36) Top = area.Bottom - Height - 12;
        }
        KeepOnScreen(); Save();
    }
    private void ResizeDrag(object sender, DragDeltaEventArgs e) { Width = Math.Clamp(Width + e.HorizontalChange, 210, 440); Height = Math.Clamp(Height + e.VerticalChange, 250, 520); }
    private void ResizeDone(object sender, DragCompletedEventArgs e) { KeepOnScreen(); Save(); }
    private void CornerClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(28, 37, 32)), BorderBrush = Brushes.SeaGreen };
        foreach (string corner in new[] { "Top left", "Top right", "Bottom left", "Bottom right" })
        {
            var item = new MenuItem { Header = corner };
            item.Click += (_, _) => Snap(corner); menu.Items.Add(item);
        }
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    public void Snap(string corner)
    {
        if(quietWindow!=null && settings.Attached){settings.DockCorner=corner;PlaceAttached();Save();return;}
        Rect area = WorkArea();
        Left = corner.EndsWith("right", StringComparison.Ordinal) ? area.Right - Width - 12 : area.Left + 12;
        Top = !settings.Backdrop ? area.Bottom-Height : corner.StartsWith("Bottom", StringComparison.Ordinal) ? area.Bottom - Height - 12 : area.Top + 12;
        KeepOnScreen(); Save();
    }
    private void KeepOnScreen()
    {
        Rect area = WorkArea();
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
    internal static double DesktopPosition(double x, double left, double width) => width>0 ? Math.Clamp((x-left)/width,0,1) : .5;
    private double lastSpatialPosition = double.NaN;
    private void UpdateSpatialPosition()
    {
        if (!RiggedAvatar.Ready || !IsVisible || PresentationSource.FromVisual(this) == null) return;
        // Use one physical-pixel coordinate system across every monitor, including negative origins.
        Point center=PointToScreen(new Point(ActualWidth/2,ActualHeight/2));
        double position=DesktopPosition(center.X,GetSystemMetrics(76),GetSystemMetrics(78));
        if (double.IsNaN(lastSpatialPosition) || Math.Abs(position-lastSpatialPosition)>.0001)
        { lastSpatialPosition=position;RiggedAvatar.SetSpatialPosition(position); }
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private Rect WorkArea()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var a = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var b = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(a, b);
    }
    private void Save()
    {
        if(quietWindow==null || !settings.Attached){settings.Left = Left; settings.Top = Top;settings.Pinned = Topmost;}
        settings.Collapsed = Collapsed;
        settings.Positioned = true;
        if (!Collapsed) { settings.Width = Width; settings.Height = Height; }
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!); File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings)); } catch { }
    }
    private sealed class CompanionSettings
    {
        public double Width { get; set; } = 230;
        public double Height { get; set; } = 284;
        public double Left { get; set; } = -1;
        public double Top { get; set; } = -1;
        public bool Positioned { get; set; }
        public bool Visible { get; set; } = true;
        public bool Pinned { get; set; } = true;
        public bool Collapsed { get; set; }
        public string? ImageFolder { get; set; }
        public bool LayeredEyes { get; set; } = true;
        public bool FollowMouse { get; set; } = true;
        public bool RiggedMode { get; set; } = true;
        public bool Attached { get; set; } = true;
        public string DockCorner { get; set; } = "Bottom right";
        public bool DesktopAlwaysVisible { get; set; } = true;
        public bool SnapEdges { get; set; } = true;
        public bool DoubleClickOpensQuiet { get; set; } = true;
        public bool SpeechBubbles { get; set; } = true;
        public bool ThinkingIndicator { get; set; } = true;
        public bool Backdrop { get; set; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
}

