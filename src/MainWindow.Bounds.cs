using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuietGPT;

public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowBoundsHook);
        Dispatcher.BeginInvoke(RefreshMaximizedWorkArea);
    }

    private IntPtr WindowBoundsHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x001A or 0x007E) // WM_SETTINGCHANGE / WM_DISPLAYCHANGE
            Dispatcher.BeginInvoke(RefreshMaximizedWorkArea);
        if (message != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO
        var monitor = new BoundsMonitorInfo { Size = Marshal.SizeOf<BoundsMonitorInfo>() };
        if (!GetMonitorInfoForBounds(MonitorFromWindowForBounds(hwnd, 2), ref monitor)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<BoundsMinMaxInfo>(lParam);
        ApplyWorkArea(ref limits, monitor.Monitor, monitor.Work);
        // The native message uses physical pixels, including on displays with scaled DPI.
        double scale = GetDpiForWindow(hwnd) / 96.0;
        limits.MinTrackSize.X = (int)Math.Ceiling(MinWidth * scale);
        limits.MinTrackSize.Y = (int)Math.Ceiling(MinHeight * scale);
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void RefreshMaximizedWorkArea()
    {
        if (closing || WindowState != WindowState.Maximized) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new BoundsMonitorInfo { Size = Marshal.SizeOf<BoundsMonitorInfo>() };
        if (!GetMonitorInfoForBounds(MonitorFromWindowForBounds(hwnd, 2), ref info)) return;
        SetWindowPosForBounds(hwnd, IntPtr.Zero, info.Work.Left, info.Work.Top,
            info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, 0x0014); // NOZORDER | NOACTIVATE
    }

    private static void ApplyWorkArea(ref BoundsMinMaxInfo limits, BoundsRect monitor, BoundsRect work)
    {
        limits.MaxPosition = new BoundsPoint { X = work.Left - monitor.Left, Y = work.Top - monitor.Top };
        limits.MaxSize = new BoundsPoint { X = work.Right - work.Left, Y = work.Bottom - work.Top };
        // Keep normal resizing limits intact so restoring and dragging between monitors still work.
    }

    [StructLayout(LayoutKind.Sequential)] private struct BoundsPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BoundsRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BoundsMonitorInfo { public int Size; public BoundsRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BoundsMinMaxInfo { public BoundsPoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")] private static extern IntPtr MonitorFromWindowForBounds(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoForBounds(IntPtr monitor, ref BoundsMonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out BoundsRect rect);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool SetWindowPosForBounds(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private delegate bool BoundsMonitorCallback(IntPtr monitor, IntPtr dc, ref BoundsRect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, BoundsMonitorCallback callback, IntPtr data);
}
