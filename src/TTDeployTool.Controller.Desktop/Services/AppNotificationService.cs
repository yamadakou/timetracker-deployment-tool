using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>
/// Windows の通知（Windows App SDK の AppNotificationManager。docs/design/desktop-jobs.md の 5.1）。押すとジョブを開けるよう、ジョブの ID を引数に入れる。
/// </summary>
internal sealed class AppNotificationService : INotificationService
{
    /// <summary>通知の引数の、開くジョブの ID のキー。</summary>
    public const string JobArgument = "job";

    public void Show(AppNotice notice)
    {
        var builder = new AppNotificationBuilder().AddText(notice.Title).AddText(notice.Message);
        if (notice.JobId != null)
            builder.AddArgument(JobArgument, notice.JobId);
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
