using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace QuietGPT;

public partial class MainWindow
{
    private readonly List<DownloadRecord> downloadHistory = [];
    private Popup? downloadPopup;
    private string downloadCloseReason="not opened";
    private StackPanel? downloadRows;
    private readonly Dictionary<DownloadRecord, TextBlock> downloadLabels = [];
    private static readonly Brush PanelText = new SolidColorBrush(Color.FromRgb(237, 243, 239));
    private string DownloadHistoryPath => Path.Combine(dataRoot, "downloads.json");

    private void LoadDownloadHistory()
    {
        try
        {
            if (File.Exists(DownloadHistoryPath))
                downloadHistory.AddRange((JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(DownloadHistoryPath)) ?? []).Take(100));
        }
        catch { }
    }

    private void SaveDownloadHistory()
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(DownloadHistoryPath, JsonSerializer.Serialize(downloadHistory.Where(d => d.Completed).Take(100)));
        }
        catch { }
    }

    private DownloadRecord TrackDownload(CoreWebView2DownloadOperation operation)
    {
        var record = new DownloadRecord { FilePath = operation.ResultFilePath, Operation = operation };
        downloadHistory.Insert(0, record);
        RefreshDownloadRows();
        return record;
    }

    private void ShowDownloads()
    {
        if (downloadPopup == null)
        {
            var root = new StackPanel();
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var close = MakeDownloadButton("×", () => downloadPopup!.IsOpen = false);
            close.ToolTip = "Close downloads";
            DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
            heading.Children.Add(new TextBlock { Text = "Downloads", Foreground = PanelText, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(heading);
            downloadRows = new StackPanel();
            root.Children.Add(new ScrollViewer { Content = downloadRows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            footer.Children.Add(MakeDownloadButton("Downloads folder", () =>
            {
                string? folder = Browser.CoreWebView2?.Profile.DefaultDownloadFolderPath;
                if (!string.IsNullOrEmpty(folder)) OpenDownloadPath(folder, false);
            }));
            footer.Children.Add(MakeDownloadButton("Browser history…", () =>
            {
                downloadPopup!.IsOpen = false;
                Dispatcher.BeginInvoke(() =>
                {
                    if (closing || Browser.CoreWebView2 is not { } core) return;
                    core.DefaultDownloadDialogCornerAlignment = CoreWebView2DefaultDownloadDialogCornerAlignment.TopRight;
                    core.DefaultDownloadDialogMargin = new System.Drawing.Point(12, 12);
                    core.OpenDefaultDownloadDialog();
                });
            }));
            root.Children.Add(footer);
            downloadPopup = new Popup
            {
                PlacementTarget = DownloadsButton, Placement = PlacementMode.Custom, StaysOpen = false,
                AllowsTransparency = true,
                CustomPopupPlacementCallback = (size, target, offset) => [new CustomPopupPlacement(new Point(target.Width - size.Width, target.Height + 5), PopupPrimaryAxis.Horizontal)],
                Child = new Border { Width = 440, Padding = new Thickness(18), Background = new SolidColorBrush(Color.FromRgb(28, 37, 32)), BorderBrush = new SolidColorBrush(Color.FromRgb(90, 117, 101)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = root }
            };
            downloadPopup.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { downloadPopup.IsOpen = false; e.Handled = true; } };
            downloadPopup.Closed += (_,_) => { if(DownloadsButton.IsMouseOver)downloadsDismissedAt=DateTime.UtcNow; };
            LocationChanged += (_, _) => {downloadCloseReason="window moved";downloadPopup.IsOpen = false;};
            SizeChanged += (_, _) => {downloadCloseReason="window resized";downloadPopup.IsOpen = false;};
        }
        RefreshDownloadRows();
        downloadPopup.IsOpen = true;
        downloadCloseReason="opened (later dismissal is focus or pointer)";
        unseenDownloads = 0;
        UpdateDownloadIndicator();
    }

    private static Button MakeDownloadButton(string label, Action action)
    {
        var button = new Button { Content = label, Foreground = PanelText, Background = new SolidColorBrush(Color.FromRgb(43, 62, 50)), Margin = new Thickness(0, 0, 7, 0), FontSize = 12, Padding = new Thickness(9, 0, 9, 0) };
        button.Click += (_, _) => action();
        return button;
    }

    private void RefreshDownloadRows()
    {
        if (downloadRows is null) return;
        downloadRows.Children.Clear(); downloadLabels.Clear();
        if (downloadHistory.Count == 0)
            downloadRows.Children.Add(new TextBlock { Text = "Your next downloads will appear here.\nOlder downloads are in Browser history below.", Foreground = PanelText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        foreach (var record in downloadHistory.Take(100))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            row.Children.Add(new TextBlock { Text = Path.GetFileName(record.FilePath), ToolTip = record.FilePath, Foreground = PanelText, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(187, 207, 195)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 9) };
            downloadLabels[record] = status; row.Children.Add(status); UpdateDownloadRow(record);
            var actions = new WrapPanel();
            if (record.Completed)
            {
                var open = MakeDownloadButton("Open file", () => OpenDownloadPath(record.FilePath, false));
                open.IsEnabled = File.Exists(record.FilePath); actions.Children.Add(open);
                var folder = MakeDownloadButton("Show in folder", () => OpenDownloadPath(record.FilePath, true));
                folder.IsEnabled = Directory.Exists(Path.GetDirectoryName(record.FilePath)); actions.Children.Add(folder);
            }
            else if (record.Operation is { } operation)
            {
                if (operation.State == CoreWebView2DownloadState.InProgress)
                    actions.Children.Add(MakeDownloadButton("Cancel", () => operation.Cancel()));
                else if (operation.CanResume)
                    actions.Children.Add(MakeDownloadButton("Resume", () => { operation.Resume(); RefreshDownloadRows(); }));
            }
            row.Children.Add(actions); downloadRows.Children.Add(row);
        }
    }

    private void UpdateDownloadRow(DownloadRecord record)
    {
        if (!downloadLabels.TryGetValue(record, out var label)) return;
        if (record.Completed) label.Text = File.Exists(record.FilePath) ? "Completed" : "File moved or removed";
        else if (record.Operation is { } operation)
            label.Text = operation.State == CoreWebView2DownloadState.Interrupted ? $"Needs attention: {operation.InterruptReason} · Browser history has more controls"
                : operation.TotalBytesToReceive is > 0 ? $"Downloading · {100.0 * operation.BytesReceived / operation.TotalBytesToReceive:F0}%"
                : $"Downloading · {operation.BytesReceived / 1048576.0:F1} MB";
    }

    private void OpenDownloadPath(string path, bool reveal)
    {
        try
        {
            if (reveal)
            {
                if (!File.Exists(path)) path = Path.GetDirectoryName(path)!;
                if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) throw new IOException("The folder is no longer available.");
                Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"") { UseShellExecute = true });
            }
            else
            {
                if (!File.Exists(path) && !Directory.Exists(path)) throw new IOException("The file was moved or removed.");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) { SetStatus("Could not open download: " + ex.Message); }
    }

    private sealed class DownloadRecord
    {
        public string FilePath { get; set; } = "";
        public bool Completed { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public CoreWebView2DownloadOperation? Operation { get; set; }
    }
}
