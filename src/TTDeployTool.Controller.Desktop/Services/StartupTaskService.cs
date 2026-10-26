using TTDeployTool.Controller.Desktop.Core.Services;
using Windows.ApplicationModel;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>
/// Windows のサインイン時の起動（<see cref="StartupTask"/>。マニフェストの windows.startupTask。docs/design/desktop-jobs.md の 6.5）。
/// パッケージのデスクトップ アプリでは、<see cref="StartupTask.RequestEnableAsync"/> で同意のダイアログは出ない。
/// パッケージで動いていない場合などは、使えないものとして扱う。
/// </summary>
internal sealed class StartupTaskService : IStartupTaskService
{
    /// <summary>マニフェストの uap5:StartupTask の TaskId。</summary>
    public const string TaskId = "TTDeployToolStartup";

    public async Task<StartupTaskStatus> GetStatusAsync()
    {
        try
        {
            return StartupTaskStates.FromWindowsState((await StartupTask.GetAsync(TaskId)).State.ToString());
        }
        catch (Exception)
        {
            return StartupTaskStatus.Unavailable;
        }
    }

    public async Task<StartupTaskStatus> EnableAsync() =>
        StartupTaskStates.FromWindowsState((await (await StartupTask.GetAsync(TaskId)).RequestEnableAsync()).ToString());

    public async Task DisableAsync() => (await StartupTask.GetAsync(TaskId)).Disable();
}
