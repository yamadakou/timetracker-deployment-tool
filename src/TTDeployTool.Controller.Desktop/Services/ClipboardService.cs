using TTDeployTool.Controller.Desktop.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>
/// クリップボード。パスワードをコピーするため、クリップボードの履歴とほかの PC への同期に残さない
/// （ClipboardContentOptions。Windows 10 1809 以降。docs/design/desktop-jobs.md の 6.4）。
/// </summary>
internal sealed class ClipboardService : IClipboardService
{
    public void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
    }
}
