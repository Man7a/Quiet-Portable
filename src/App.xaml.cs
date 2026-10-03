using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Text;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QuietGPT;

public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args.Contains("--smoke-test");
        instance = new Mutex(true, PortablePaths.InstanceName(smoke), out bool first);
        if (!first)
        {
            // MainWindowHandle deliberately excludes hidden windows. Enumerate them
            // too, otherwise a hidden launch permanently swallows later shortcuts.
            for (int attempt=0;attempt<20;attempt++)
            {
                foreach (var process in Process.GetProcessesByName("QuietGPT"))
                {
                    using(process)
                    {
                        if(process.Id==Environment.ProcessId)continue;
                        try
                        {
                            var processPath=process.MainModule?.FileName;
                            
                            if(!string.Equals(processPath,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))continue;
                            var handle=FindMainWindow(process.Id);
                            if(handle==IntPtr.Zero)continue;
                            if(SendMessageTimeout(handle,0,IntPtr.Zero,IntPtr.Zero,2,1000,out _)==IntPtr.Zero)continue;
                            ShowWindowAsync(handle,IsIconic(handle)?9:5);
                            SetForegroundWindow(handle);
                            LogStartup("Recovered existing window for process "+process.Id);
                            Shutdown();return;
                        }
                        catch { /* An exiting process is retried without touching its profile. */ }
                    }
                }
                await Task.Delay(250);
            }
            MessageBox.Show("Quiet is already running, but its window could not be restored. If it is closing, wait a moment and try again. Otherwise end QuietGPT in Task Manager and reopen it. Your saved chats and settings are not removed.","Quiet could not restore its window",MessageBoxButton.OK,MessageBoxImage.Information);
            LogStartup("Existing instance could not be restored");
            Shutdown();
            return;
        }
        try { Directory.CreateDirectory(PortablePaths.Data); var probe=Path.Combine(PortablePaths.Data,".write-test");File.WriteAllText(probe,"");File.Delete(probe); }
        catch(Exception ex) { MessageBox.Show("Extract Quiet into a writable folder, such as Documents. It cannot save its portable profile here.\n"+ex.Message,"Quiet Portable");Shutdown(1);return; }
        var window = new MainWindow(smoke);
        MainWindow = window;
        window.Show();
        // A launcher can pass SW_HIDE in STARTUPINFO. A second explicit show after
        // the first WPF Show has consumed that flag ensures normal launches appear.
        if(!smoke)_ = window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
        {
            var handle=new WindowInteropHelper(window).Handle;
            ShowWindow(handle,window.WindowState==WindowState.Maximized?3:5);
            window.Activate();
            LogStartup("Main window shown");
        }));
    }
    internal static IntPtr FindMainWindow(int processId)
    {
        IntPtr found=IntPtr.Zero;
        EnumWindows((handle,_)=>
        {
            GetWindowThreadProcessId(handle,out uint pid);
            if(pid!=(uint)processId)return true;
            var title=new StringBuilder(256);GetWindowText(handle,title,title.Capacity);
            if(title.ToString()!="Quiet · ChatGPT")return true;
            found=handle;return false;
        },IntPtr.Zero);
        return found;
    }
    private static void LogStartup(string message)
    {
        try
        {
            var folder=PortablePaths.Data;
            Directory.CreateDirectory(folder);
            var path=Path.Combine(folder,"startup.log");
            if(File.Exists(path)&&new FileInfo(path).Length>65536)File.WriteAllText(path,"");
            File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine);
        }
        catch { }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    private delegate bool WindowCallback(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback,IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wParam,IntPtr lParam,uint flags,uint timeout,out IntPtr result);
}
