using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Blazor.TestHost;

/// <summary>コピーした最後の文字を残す（テストが /test/clipboard で読む）。</summary>
public sealed class TestClipboard : IClipboardService
{
    private string _last = "";

    public string Last => Volatile.Read(ref _last);

    public void CopyText(string text) => Volatile.Write(ref _last, text);
}
