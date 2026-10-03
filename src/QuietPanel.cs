using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;

namespace QuietGPT;

// A theme scoped to auxiliary panels, keeping Quiet's compact main toolbar intact.
internal static class QuietPanel
{
    internal static void Apply(Window window)
    {
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/QuietGPT;component/SettingsTheme.xaml", UriKind.Relative)
        });
        window.Style = (Style)window.FindResource("QuietPanelWindow");
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.CanResize;
        window.MinWidth = 440;
        window.MaxHeight = SystemParameters.WorkArea.Height;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 44, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(10),
            UseAeroCaptionButtons = false
        });
        window.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
            (_, _) => window.Close()));
    }
}
