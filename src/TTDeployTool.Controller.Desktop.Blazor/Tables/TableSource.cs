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
/// <param name="Cell">セルの表示（文字・ツールチップ・色。WinUI の画面のセルのテンプレートと同じく、行の ViewModel の文字を出す）</param>
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
    double FillMinWidth,
    Func<TRow, string, CellView> Cell)
    where TRow : class
    where TItem : class
{
    /// <summary>セルに見えている文字（Core の列の文字。コピーと同じ）。</summary>
    public string CellText(TRow row, string key) => Filters.Column(key).TextOf(ItemOf(row));
}

/// <summary>セルの表示（<see cref="ToolTip"/> があれば、6 行で切れたかによらず出す。<see cref="Css"/> は色など）。</summary>
public sealed record CellView(string? Text, string? ToolTip = null, string? Css = null);

/// <summary>今の 2 つの一覧の表（列の配置は MainViewModel が持つ。右端の列の最小の幅は WinUI の画面と同じ。desktop-tables.md の 10.5）。</summary>
public static class TableSources
{
    public static TableSource<DeploymentRowViewModel, TTDeployTool.Application.Inventory.DeploymentEntry> Deployments(DeploymentListViewModel list, TableLayout layout) =>
        new("deployments", list.Filters, layout, list.Rows, r => r.Entry, DeploymentListViewModel.CanSortBy,
            () => list.SortColumn, () => list.SortDescending, list.SortBy, list.ClearSort, () => list.Selected, list.SetSelection, 160, DeploymentCell);

    public static TableSource<JobViewModel, JobViewModel> Jobs(JobListViewModel list, TableLayout layout) =>
        new("jobs", list.Filters, layout, list.Items, j => j, JobListViewModel.CanSortBy,
            () => list.SortColumn, () => list.SortDescending, list.SortBy, list.ClearSort, () => list.Selected, list.SetSelection, 320, JobCell);

    // デプロイの一覧のセル（WinUI の画面と同じ: グループは食い違いの注意、状態は各アプリの状態をツールチップに。期限は近さで色を変える）
    internal static CellView DeploymentCell(DeploymentRowViewModel row, string key) => key switch
    {
        "Subscription" => new(row.Subscription),
        "ResourceGroup" => new(row.ResourceGroup),
        "AppName" => new(row.AppName, Css: "tt-strong"),
        "Group" => new(row.GroupText, row.GroupWarning),
        "State" => new(row.StateText, row.StateDetail),
        "ExpiresAt" => new(row.ExpiryText, Css: row.Level switch { ExpiryLevel.Expired => "tt-critical", ExpiryLevel.Soon => "tt-caution", _ => null }),
        "DeployedAt" => new(row.DeployedText),
        _ => new(row.JobText, Css: "tt-caution"),
    };

    // ジョブの一覧のセル（WinUI の画面と同じく、行の ViewModel の文字）
    internal static CellView JobCell(JobViewModel job, string key) => new(key switch
    {
        "Kind" => job.KindText,
        "Subscription" => job.SubscriptionText,
        "Target" => job.TargetText,
        "State" => job.StatusText,
        "Time" => job.TimeText,
        "RegisteredAt" => job.RegisteredText,
        "ChangedAt" => job.ChangedText,
        "StartedAt" => job.StartedText,
        "Finished" => job.FinishedText,
        "Duration" => job.DurationText,
        _ => job.ResultText,
    });
}
