using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
namespace QuietGPT;
public partial class CompanionWindow
{
    private void ShoulderDrag(bool start)
    {
        // WebView pointer capture is released when Windows takes ownership.
        // That cancellation must not finish the native move loop early.
        if(!start||dragging||(GetAsyncKeyState(1)&0x8000)==0)return;
        dragging=true;
        Dispatcher.BeginInvoke(() => {
            try
            {
                if(closed || (GetAsyncKeyState(1)&0x8000)==0)return;
                ReleaseCapture();
                SendMessage(new WindowInteropHelper(this).Handle,0x0112,(IntPtr)0xF012,IntPtr.Zero);
            }
            finally { EndShoulderDrag(); }
        });
    }
    private void EndShoulderDrag()
    {
        if(!dragging)return;
        dragging=false;
        if(settings.Attached&&quietWindow!=null){var r=QuietBounds();settings.DockCorner=(Top+Height/2<r.Top+r.Height/2?"Top ":"Bottom ")+(Left+Width/2<r.Left+r.Width/2?"left":"right");PlaceAttached();}
        else if(settings.SnapEdges){var r=WorkArea();if(settings.Backdrop){if(Math.Abs(Left-r.Left)<36)Left=r.Left+12;if(Math.Abs(Left+Width-r.Right)<36)Left=r.Right-Width-12;if(Math.Abs(Top-r.Top)<36)Top=r.Top+12;}if(Math.Abs(Top+Height-r.Bottom)<60)Top=r.Bottom-Height-(settings.Backdrop?12:0);}
        KeepOnScreen();Save();
    }
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
}
