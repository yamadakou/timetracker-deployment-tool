using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// アプリの入口。1 つだけ起動し、2 つ目の起動は 1 つ目に渡す（常駐中にスタート メニューから起動すると、ジョブを持たない別のアプリが
/// 動いてしまうため。手順は Microsoft Learn「How to create a single-instanced WinUI app with C#」のとおり。docs/design/desktop-jobs.md の 5.1）。
/// </summary>
public static class Program
{
    // このアプリを 1 つにするための鍵
    private const string InstanceKey = "ttdeploytool-desktop";

    /// <summary>2 つ目の起動が渡されたとき（Windows App SDK のスレッドで呼ばれる）。</summary>
    public static event EventHandler? Reactivated;

    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            RedirectTo(instance);
            return 0;
        }
        instance.Activated += (_, _) => Reactivated?.Invoke(null, EventArgs.Empty);
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    // 1 つ目に起動を渡す。渡し終わるまで、COM の呼び出しを止めない待ち方で待ち、1 つ目のウィンドウを前に出す
    private static void RedirectTo(AppInstance instance)
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var redirected = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            instance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirected);
        });
        const uint CwmoDefault = 0;
        const uint Infinite = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(CwmoDefault, Infinite, 1, [redirected], out _);
        SetForegroundWindow(Process.GetProcessById((int)instance.ProcessId).MainWindowHandle);
    }

    // Microsoft Learn の手順と同じ宣言
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
