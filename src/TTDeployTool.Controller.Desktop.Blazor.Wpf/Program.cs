using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// アプリの入口。WinUI 3 のアプリの Program.cs と同じく、1 つだけ起動し、2 つ目の起動は 1 つ目に渡す（Windows App SDK の AppInstance。
/// 移行で機能が落ちないかの確認。docs/design/desktop/desktop-blazor-hybrid.md の 15）。鍵は本番のアプリと分ける。
/// </summary>
public static class Program
{
    private const string InstanceKey = "ttdeploytool-blazor-prototype";

    /// <summary>2 つ目の起動が渡されたとき（Windows App SDK のスレッドで呼ばれる）。</summary>
    public static event EventHandler? Reactivated;

    [STAThread]
    public static int Main(string[] args)
    {
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            RedirectTo(instance);
            return 0;
        }
        instance.Activated += (_, _) => Reactivated?.Invoke(null, EventArgs.Empty);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    // 1 つ目に起動を渡す。渡し終わるまで、COM の呼び出しを止めない待ち方で待ち、1 つ目のウィンドウを前に出す（WinUI のアプリと同じ）
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr eventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
