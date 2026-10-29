namespace TTDeployTool.Controller.Desktop.Core.Diagnostics;

/// <summary>
/// 画面の部品の中で起きる、続けて動かしてよいと分かっている例外（処理されなかった例外として記録し、アプリは終了させない）。
/// </summary>
public static class KnownUiErrors
{
    /// <summary>
    /// WinUI.TableView 1.5.1 は、行を 1 つだけ選ぶと、その行の番号で <c>ScrollRowIntoView</c> を後から実行する。それまでに表の行が減ると、
    /// 番号が範囲の外になり、例外になる（行を選んだデプロイを削除した直後に、一覧を続けて作り直したとき。Desktop System Test、2026-10-29）。
    /// 選んだ行を表示させる処理が 1 回行われないだけのため、続けて動かす。
    /// </summary>
    public static bool IsTableViewScrollRace(Exception exception) =>
        exception is ArgumentOutOfRangeException && exception.StackTrace?.Contains("WinUI.TableView.TableView.ScrollRowIntoView", StringComparison.Ordinal) == true;
}
