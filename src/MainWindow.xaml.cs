using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace QuietGPT;

public partial class MainWindow : Window
{
    private const string Home = "https://chatgpt.com/";
    private readonly bool smokeTest;
    private readonly string dataRoot;
    private CoreWebView2Environment? environment;
    private Window? signInWindow;
    private readonly HashSet<CoreWebView2DownloadOperation> downloads = [];
    private int unseenDownloads;
    private int ActiveDownloads => downloads.Count(d => d.State == CoreWebView2DownloadState.InProgress);
    private bool initializing;
    private bool closing;
    private bool shutdownStarted;
    private bool shutdownReady;
    private ContextMenu? moreMenu;
    private DateTime menuDismissedAt, downloadsDismissedAt;
    private Preferences preferences = new();

    public MainWindow(bool smoke = false)
    {
        smokeTest = smoke;
        dataRoot = smoke ? Path.Combine(AppContext.BaseDirectory, "test-results", "profile-test")
            : PortablePaths.Data;
        InitializeComponent();
        LoadDownloadHistory();
        try
        {
            if (File.Exists(Path.Combine(dataRoot, "settings.json")))
                preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(dataRoot, "settings.json"))) ?? new();
            Width = Math.Clamp(preferences.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(preferences.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            if (preferences.Maximized) WindowState = WindowState.Maximized;
        }
        catch { /* An unreadable preference file does not prevent startup. */ }
        StateChanged += (_, _) =>
        {
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        };
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        int dark = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        await InitializeBrowser();
    }

    private async Task InitializeBrowser()
    {
        if (initializing || closing) return;
        initializing = true;
        RetryButton.Visibility = Visibility.Collapsed;
        StartMessage.Text = "Opening ChatGPT…";
        try
        {
            Directory.CreateDirectory(dataRoot);
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(dataRoot, "Profile"));
            await Browser.EnsureCoreWebView2Async(environment);
            if (closing) return;
            var core = Browser.CoreWebView2;
            Configure(core);
            await InitializeCompanion(core);
            await InitializeAutoRead();
            await InitializeRayBridge();
            InitializeAutomaticBridge();
            Browser.ZoomFactor = Math.Clamp(preferences.Zoom, 0.5, 2);
            core.NavigationStarting += NavigationStarting;
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    StartPanel.Visibility = Visibility.Collapsed;
                    Browser.Visibility = Visibility.Visible;
                    SetStatus(ActiveDownloads > 0 ? $"Downloading {ActiveDownloads} file(s)…" : "Ready");
                    // Background navigation must not take focus from the current input.
                }
                else if (e.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted && Browser.Visibility == Visibility.Visible)
                {
                    // Attachment navigation aborts without replacing the existing document.
                    // Keep that document (and its conversation state) available.
                    SetStatus("Page kept open · If a link did not load, try it again");
                }
                else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    ShowError($"ChatGPT could not load ({e.WebErrorStatus}). Check your connection and try again.");
                }
                UpdateLocation();
            };
            core.HistoryChanged += (_, _) => BackButton.IsEnabled = core.CanGoBack;
            core.SourceChanged += (_, _) => UpdateLocation();
            core.NewWindowRequested += NewWindowRequested;
            core.DownloadStarting += DownloadStarting;
            core.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    RetryButton.Visibility = Visibility.Collapsed;
                    StartMessage.Text = "The web engine stopped. Close Quiet and open it again; your profile is saved.";
                    Browser.Visibility = Visibility.Hidden;
                    StartPanel.Visibility = Visibility.Visible;
                    SetStatus("Restart Quiet to reconnect");
                }
                else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                    ShowError("The page stopped responding. Reload to continue.");
            };
            if (smokeTest) await RunSmokeTestsAsync();
            else core.Navigate(StartupAddress());
        }
        catch (Exception e)
        {
            ShowError(e is WebView2RuntimeNotFoundException
                ? "Microsoft Edge WebView2 Runtime is missing. Install the Evergreen Runtime from Microsoft, then reopen Quiet."
                : $"Quiet could not start: {e.Message}");
            if (smokeTest)
            {
                Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-results"));
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-results", "startup-error.txt"), e.ToString());
                Application.Current.Shutdown(1);
            }
        }
        finally { initializing = false; }
    }

    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
        core.PermissionRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !NavigationPolicy.IsHost(uri.IdnHost, "chatgpt.com"))
                e.State = CoreWebView2PermissionState.Deny;
            // ChatGPT uses the web engine's normal permission prompt for microphone/camera.
        };
        core.ContextMenuRequested += (_, e) =>
        {
            for (int i = e.MenuItems.Count - 1; i >= 0; i--)
                if (e.MenuItems[i].Name is "openLinkInNewWindow" or "openLinkInNewTab" or "openLinkInInPrivateWindow" or "openImageInNewTab" or "openFrameInNewWindow")
                    e.MenuItems.RemoveAt(i);
        };
    }

    private bool CanStay(string uri) => NavigationPolicy.IsInternal(uri) ||
        (smokeTest && Uri.TryCreate(uri.StartsWith("blob:") ? uri[5..] : uri, UriKind.Absolute, out var test) && test.Scheme == "https" && test.Host == "quiet.test");

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!CanStay(e.Uri))
        {
            e.Cancel = true;
            if (e.IsUserInitiated) OpenExternal(e.Uri);
            else SetStatus("This link is outside ChatGPT. Open it in your browser if needed.");
            return;
        }
        SetStatus("Loading…");
    }

    private async void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (NavigationPolicy.IsAuthentication(e.Uri))
        {
            using var deferral = e.GetDeferral();
            if (signInWindow != null) { signInWindow.Activate(); return; }
            var auth = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(33, 33, 33) };
            var dialog = new Window { Title = "Sign in · Quiet", Width = 520, Height = 720, MinWidth = 420, MinHeight = 480,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Content = auth, Icon = Icon };
            signInWindow = dialog;
            dialog.Closed += (_, _) => { auth.Dispose(); signInWindow = null; };
            try
            {
                dialog.Show();
                await auth.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));
                if (closing || signInWindow != dialog) return;
                Configure(auth.CoreWebView2);
                auth.CoreWebView2.NavigationStarting += NavigationStarting;
                auth.CoreWebView2.SourceChanged += (_, _) =>
                {
                    if (Uri.TryCreate(auth.Source?.ToString(), UriKind.Absolute, out var location)) dialog.Title = $"{location.IdnHost} · Sign in";
                };
                auth.CoreWebView2.NewWindowRequested += (_, request) => request.Handled = true;
                auth.CoreWebView2.WindowCloseRequested += (_, _) => dialog.Close();
                e.NewWindow = auth.CoreWebView2;
            }
            catch { dialog.Close(); SetStatus("Sign-in could not open. Try signing in directly on ChatGPT."); }
        }
        else if (CanStay(e.Uri))
        {
            string address = e.Uri;
            _ = Dispatcher.BeginInvoke(() => { if (!closing) Browser.CoreWebView2.Navigate(address); });
        }
        else if (e.IsUserInitiated) OpenExternal(e.Uri);
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true; // Save normally, but open the download flyout only on request.
        var download = e.DownloadOperation;
        var record = TrackDownload(download);
        downloads.Add(download);
        UpdateDownloadIndicator();
        SetStatus($"Downloading {ActiveDownloads} file(s)…");
        EventHandler<object>? changed = null;
        changed = (_, _) =>
        {
            if (closing) return;
            record.FilePath = download.ResultFilePath;
            UpdateDownloadIndicator();
            if (download.State == CoreWebView2DownloadState.InProgress)
            { SetStatus($"Downloading {ActiveDownloads} file(s)…"); RefreshDownloadRows(); return; }
            if (download.State == CoreWebView2DownloadState.Completed)
            {
                download.StateChanged -= changed;
                download.BytesReceivedChanged -= progress;
                downloads.Remove(download);
                unseenDownloads++;
                record.Completed = true;
                record.Operation = null;
                foreach (var old in downloadHistory.Where(d => d.Completed).Skip(100).ToArray()) downloadHistory.Remove(old);
                SaveDownloadHistory();
                ReactCompanion("download");
            }
            else ReactCompanion("error");
            RefreshDownloadRows();
            UpdateDownloadIndicator();
            SetStatus(download.State == CoreWebView2DownloadState.Completed
                ? "Download complete · Ctrl+J to view"
                : $"Download interrupted: {download.InterruptReason} · Ctrl+J to retry");
        };
        download.StateChanged += changed;
        download.BytesReceivedChanged += progress;
        void progress(object? source, object args) { if (!closing) { UpdateDownloadIndicator(); UpdateDownloadRow(record); } }
    }

    private void UpdateDownloadIndicator()
    {
        int active = ActiveDownloads;
        bool interrupted = downloads.Any(d => d.State == CoreWebView2DownloadState.Interrupted);
        DownloadBadge.Text = active > 0 ? active.ToString() : interrupted ? "!" : unseenDownloads > 0 ? "✓" : "";
        DownloadBadge.Visibility = DownloadBadge.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadBadge.Foreground = interrupted ? Brushes.SandyBrown : new SolidColorBrush(Color.FromRgb(183, 228, 202));
        string detail;
        if (active > 0)
        {
            var running = downloads.Where(d => d.State == CoreWebView2DownloadState.InProgress).ToArray();
            long received = running.Sum(d => d.BytesReceived);
            double total = running.Sum(d => (double)(d.TotalBytesToReceive ?? 0));
            detail = running.All(d => d.TotalBytesToReceive > 0)
                ? $"Downloading {active} file(s) · {Math.Clamp(100.0 * received / total, 0, 100):F0}%"
                : $"Downloading {active} file(s) · {received / 1048576.0:F1} MB";
        }
        else detail = interrupted ? "Download needs attention · Open downloads to retry or review"
            : unseenDownloads > 0 ? $"{unseenDownloads} download(s) complete" : "Downloads";
        DownloadsButton.ToolTip = detail + " (Ctrl+J)";
        System.Windows.Automation.AutomationProperties.SetName(DownloadsButton, detail);
    }

    private void OpenExternal(string address)
    {
        if (!NavigationPolicy.IsExternalWeb(address)) { SetStatus("This type of link is not supported."); return; }
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); SetStatus("Link opened in your default browser"); }
        catch { SetStatus("The link could not open in your default browser."); }
    }

    private void UpdateLocation()
    {
        if (Uri.TryCreate(Browser.Source?.ToString(), UriKind.Absolute, out var uri)) OriginLabel.Text = uri.IdnHost;
        BackButton.IsEnabled = Browser.CoreWebView2?.CanGoBack == true;
    }
    private void SetStatus(string message) => StatusText.Text = message;
    private void ShowError(string message)
    {
        companionReaction.Stop();
        companion?.SetActivity("error");
        StartMessage.Text = message;
        Browser.Visibility = Visibility.Hidden;
        StartPanel.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        SetStatus("Connection needs attention");
    }
    private void BackClick(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack(); }
    private async void ReloadClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 == null) await InitializeBrowser();
        else { StartPanel.Visibility = Visibility.Collapsed; Browser.Visibility = Visibility.Visible; Browser.Reload(); }
    }
    private void HomeClick(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Navigate(Home);
    private void DownloadsClick(object sender, RoutedEventArgs e)
    {
        if (downloadPopup?.IsOpen == true) { downloadPopup.IsOpen=false; return; }
        if (DateTime.UtcNow-downloadsDismissedAt<TimeSpan.FromMilliseconds(400)) return;
        // Defer until the toolbar/menu click finishes, so it cannot dismiss the popup it opens.
        Dispatcher.BeginInvoke(() => { if (!closing) ShowDownloads(); });
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void MenuClick(object sender, RoutedEventArgs e)
    {
        if (moreMenu?.IsOpen == true) { moreMenu.IsOpen=false; return; }
        if (DateTime.UtcNow-menuDismissedAt<TimeSpan.FromMilliseconds(400)) return;
        var menu = new ContextMenu { Background = new SolidColorBrush(Color.FromRgb(35, 43, 38)), Foreground = Foreground, BorderBrush = new SolidColorBrush(Color.FromRgb(57, 71, 62)), Padding = new Thickness(5) };
        moreMenu=menu;
        menu.Closed += (_,_) => { if(MenuButton.IsMouseOver)menuDismissedAt=DateTime.UtcNow; };
        void Item(string label, Action action) { var item = new MenuItem { Header = label, Padding = new Thickness(12, 8, 12, 8) }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Item("New chat                         Alt+Home", () => Browser.CoreWebView2?.Navigate(Home));
        Item("Downloads                       Ctrl+J", () => DownloadsClick(this, new()));
        Item(companion?.IsVisible==true ? "Hide companion" : "Show companion", () => CompanionClick(this, new()));
        Item("Ray & Mira…", ShowAutomaticBridge);
        Item("Companion settings…", () => companion?.ShowBehaviourSettings(this));
        Item("Read reply aloud…", () => ReadReplyClick(this, new()));
        var autoReadItem = new MenuItem { Header = "Auto-read new replies: " + (autoReadEnabled ? "ON" : "OFF"), IsCheckable = true, IsChecked = autoReadEnabled };
        autoReadItem.Click += (_, _) => ToggleAutoRead();
        menu.Items.Add(autoReadItem);
        Item("Stop reading", StopReading);
        menu.Items.Add(new Separator());
        Item("Zoom in                            Ctrl++", () => Zoom(0.1));
        Item("Zoom out                          Ctrl+−", () => Zoom(-0.1));
        Item($"Reset zoom ({Browser.ZoomFactor:P0})       Ctrl+0", () => Browser.ZoomFactor = 1);
        menu.Items.Add(new Separator());
        Item("About Quiet", () => MessageBox.Show(this,
            "Quiet — a little room for ChatGPT.\n\nOne page. Saved sessions. Downloads and file uploads.\n\nYour session stays on this PC. ChatGPT may ask you to sign in again when it expires. Google and some other providers may reject embedded-browser login.\n\nSource links open in your default browser. Closing Quiet stops the app; it does not run in the tray.\n\nIndependent app, not affiliated with OpenAI. Uses Microsoft's WebView2 engine.",
            "About Quiet", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.PlacementTarget = MenuButton;
        menu.IsOpen = true;
    }

    private void Zoom(double delta) => Browser.ZoomFactor = Math.Clamp(Browser.ZoomFactor + delta, 0.5, 2);
    private static bool IsShortcut(Key key, ModifierKeys modifiers) =>
        (modifiers == ModifierKeys.Control && key is Key.R or Key.J or Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0 or Key.T or Key.N) ||
        (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.R or Key.N or Key.OemPlus) ||
        (modifiers == ModifierKeys.Alt && key is Key.Left or Key.Right or Key.Home) || key == Key.F5;
    private void RunShortcut(Key key, ModifierKeys modifiers)
    {
        if (key is Key.T or Key.N) return;
        if (key is Key.R or Key.F5) ReloadClick(this, new());
        else if (key == Key.J) DownloadsClick(this, new());
        else if (key is Key.OemPlus or Key.Add) Zoom(0.1);
        else if (key is Key.OemMinus or Key.Subtract) Zoom(-0.1);
        else if (key is Key.D0 or Key.NumPad0) Browser.ZoomFactor = 1;
        else if (key == Key.Left) BackClick(this, new());
        else if (key == Key.Right && Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward();
        else if (key == Key.Home) HomeClick(this, new());
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsShortcut(key, Keyboard.Modifiers)) return;
        e.Handled = true;
        var modifiers = Keyboard.Modifiers;
        Dispatcher.BeginInvoke(() => { if (!closing) RunShortcut(key, modifiers); });
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (smokeTest || shutdownReady) return;
        if (ActiveDownloads > 0 && MessageBox.Show(this, "A download is still running. Close Quiet and interrupt it?", "Download in progress", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        e.Cancel = true;
        if (shutdownStarted) return;
        shutdownStarted = true;
        closing = true;
        StopReading();
        speechWindow?.Close();
        localSpeech.Dispose();
        companionReaction.Stop();
        companion?.Close();
        if (downloadPopup != null) downloadPopup.IsOpen = false;
        SavePreferences();
        SetStatus("Saving your session…");
        ShowInTaskbar = false;
        Hide();

        var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs>? exited = null;
        if (environment is not null)
        {
            exited = (_, _) => browserExited.TrySetResult();
            environment.BrowserProcessExited += exited;
        }
        try
        {
            signInWindow?.Close();
            Browser.Dispose();
            bool browserReleased = environment is null;
            if (environment is not null)
                browserReleased = await Task.WhenAny(browserExited.Task, Task.Delay(TimeSpan.FromSeconds(5))) == browserExited.Task;
            File.WriteAllText(Path.Combine(dataRoot, "session-save-status.json"), JsonSerializer.Serialize(new
            {
                browserReleased,
                savedUtc = DateTimeOffset.UtcNow
            }));
        }
        catch { /* A shutdown problem must never trap the user in the app. */ }
        finally
        {
            if (environment is not null && exited is not null) environment.BrowserProcessExited -= exited;
            shutdownReady = true;
            Application.Current.Shutdown();
        }
    }

    private void SavePreferences()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            preferences = new Preferences { Width = bounds.Width, Height = bounds.Height, Maximized = WindowState == WindowState.Maximized, Zoom = Browser.ZoomFactor };
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "settings.json"), JsonSerializer.Serialize(preferences));
        }
        catch { /* Closing remains possible if the preferences cannot be saved. */ }
    }
    private sealed class Preferences
    {
        public double Width { get; set; } = 1180;
        public double Height { get; set; } = 820;
        public bool Maximized { get; set; }
        public double Zoom { get; set; } = 1;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}




