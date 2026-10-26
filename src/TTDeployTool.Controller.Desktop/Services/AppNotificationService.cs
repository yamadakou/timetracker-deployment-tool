using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>
/// Windows の通知（Windows App SDK の AppNotificationManager。docs/design/desktop-jobs.md の 5.1・6.5）。押すとジョブか画面を開けるよう、
/// ジョブの ID か画面の名前を引数に入れる。
/// </summary>
internal sealed class AppNotificationService : INotificationService
{
    // Windows の設定での通知の許可（読めない環境では「確かめられない」）
    public NotificationPermission Permission
    {
        get
        {
            try
            {
                return NotificationPermissions.FromWindowsSetting(AppNotificationManager.Default.Setting.ToString());
            }
            catch (Exception)
            {
                return NotificationPermission.Unavailable;
            }
        }
    }

    public void Show(AppNotice notice)
    {
        var builder = new AppNotificationBuilder().AddText(notice.Title).AddText(notice.Message);
        if (notice.JobId != null)
            builder.AddArgument(AppNotice.JobArgument, notice.JobId);
        if (notice.Page != null)
            builder.AddArgument(AppNotice.PageArgument, notice.Page);
        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception)
        {
            // 通知が使えない環境（管理者として実行した場合など）でも、ジョブは続ける
        }
    }
}
