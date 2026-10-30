using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tables;

/// <summary>
/// 表（<see cref="DataTable{TRow, TItem}"/>）が描くもの。決まり（絞り込み・並べ替え・ページ・列の配置・コピー）は Core が持ち、表は描くことと入力を渡すことだけを行う
/// （desktop-blazor-hybrid.md の 4、Q2 の A）。<typeparamref name="TRow"/> は表の行（Core のページの行）、<typeparamref name="TItem"/> は Core の絞り込みの行。
/// </summary>
/// <param name="Rows">今のページの行（Core の一覧。変わったら描き直す）</param>
/// <param name="ItemOf">行から Core の絞り込みの行を取り出す</param>
/// <param name="CanSort">見出しで並べ替えられる列か</param>
/// <param name="SortColumn">並べ替えている列（既定の順なら null）</param>
/// <param name="SortDescending">逆順か</param>
/// <param name="Sort">列で並べ替える（列・逆順か）</param>
/// <param name="ClearSort">既定の順に戻す</param>
/// <param name="Selected">選んだ行</param>
/// <param name="SetSelection">表で選んだ行を伝える</param>
/// <param name="FillMinWidth">右端の列（残りの幅いっぱい）の、初期の最小の幅</param>
public sealed record TableSource<TRow, TItem>(
    string Name,
    TableFilters<TItem> Filters,
    TableLayout Layout,
    IReadOnlyList<TRow> Rows,
    Func<TRow, TItem> ItemOf,
    Func<string, bool> CanSort,
    Func<string?> SortColumn,
    Func<bool> SortDescending,
    Action<string, bool> Sort,
    Action ClearSort,
    Func<IReadOnlyList<TRow>> Selected,
    Action<IEnumerable<TRow>> SetSelection,
    double FillMinWidth)
    where TRow : class
    where TItem : class
{
    /// <summary>セルに見えている文字（Core の列の文字）。</summary>
    public string CellText(TRow row, string key) => Filters.Column(key).TextOf(ItemOf(row));
}

/// <summary>今の 2 つの一覧の表（列の配置は MainViewModel が持つ。右端の列の最小の幅は WinUI の画面と同じ。desktop-tables.md の 10.5）。</summary>
public static class TableSources
{
    public static TableSource<DeploymentRowViewModel, TTDeployTool.Application.Inventory.DeploymentEntry> Deployments(DeploymentListViewModel list, TableLayout layout) =>
        new("deployments", list.Filters, layout, list.Rows, r => r.Entry, DeploymentListViewModel.CanSortBy,
            () => list.SortColumn, () => list.SortDescending, list.SortBy, list.ClearSort, () => list.Selected, list.SetSelection, 160);

    public static TableSource<JobViewModel, JobViewModel> Jobs(JobListViewModel list, TableLayout layout) =>
        new("jobs", list.Filters, layout, list.Items, j => j, JobListViewModel.CanSortBy,
            () => list.SortColumn, () => list.SortDescending, list.SortBy, list.ClearSort, () => list.Selected, list.SetSelection, 320);
}
