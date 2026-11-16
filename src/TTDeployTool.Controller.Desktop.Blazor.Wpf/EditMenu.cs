using Microsoft.Web.WebView2.Core;

namespace TTDeployTool.Controller.Desktop.Blazor.Wpf;

/// <summary>
/// 文字の入力欄の右クリックのメニュー（オーナーの依頼、2026-11-16: 削除の確かめの欄に、コピーした名前を「貼り付け」たい）。BlazorWebView は、開発者ツールを使わない
/// ビルドでは WebView2 の標準の右クリックのメニューを出さないため、入力欄でだけ出し、項目を「切り取り・コピー・貼り付け・すべて選択」に絞る。入力欄でない所では出さない
/// （「戻る」「再読み込み」などでアプリが壊れないように。表と右の詳細は、アプリのメニューを出す）。
/// </summary>
internal static class EditMenu
{
    /// <summary>残す項目（WebView2 の CoreWebView2ContextMenuItem.Name）。</summary>
    internal static readonly string[] Kept = ["cut", "copy", "paste", "selectAll"];

    public static void Attach(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.ContextMenuRequested += (_, e) =>
        {
            if (!e.ContextMenuTarget.IsEditable)
            {
                e.Handled = true;
                return;
            }
            var items = e.MenuItems;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (!Kept.Contains(items[i].Name))
                    items.RemoveAt(i);
            }
        };
    }
}
