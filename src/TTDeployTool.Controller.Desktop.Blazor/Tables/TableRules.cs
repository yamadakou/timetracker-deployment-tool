using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tables;

/// <summary>見出しを押したときの並べ替えの次の状態（<see cref="Key"/> が null なら既定の順に戻す）。</summary>
public readonly record struct SortStep(string? Key, bool Descending);

/// <summary>
/// HTML の表の決まり（desktop-blazor-hybrid.md の 9）。どれも画面に依らない計算で、単体テストで確かめる。
/// </summary>
public static class HtmlTableRules
{
    /// <summary>左のチェックボックスの列の幅。</summary>
    public const double CheckColumnWidth = 40;

    /// <summary>列の最小の幅（つまみ・キーボードで縮めるとき）。</summary>
    public const double MinColumnWidth = 60;

    /// <summary>キーボード（Alt+←/→）で幅を変える量。</summary>
    public const double KeyboardStep = 16;

    /// <summary>
    /// 見出しを押したときの次の並べ替え（WinUI.TableView と同じく 3 回で戻る）: 違う列なら昇順、昇順なら降順、降順なら既定の順。
    /// 既定の順でも列が並べ替えの印を持つ表（デプロイの一覧のサブスクリプション）は、その列の昇順を既定の順とみなす。
    /// </summary>
    public static SortStep Next(string? sortColumn, bool descending, string clicked) =>
        sortColumn != clicked ? new(clicked, false) : !descending ? new(clicked, true) : new(null, false);

    /// <summary>列 <paramref name="moved"/> を <paramref name="target"/> の位置に動かした配置（同じ列・知らない列なら変えない）。</summary>
    public static IReadOnlyList<TableColumnLayout> Move(IReadOnlyList<TableColumnLayout> layout, string moved, string target)
    {
        var from = IndexOf(layout, moved);
        var to = IndexOf(layout, target);
        if (from < 0 || to < 0 || from == to)
            return layout;
        var columns = layout.ToList();
        var column = columns[from];
        columns.RemoveAt(from);
        columns.Insert(to, column);
        return columns;
    }

    /// <summary>列 <paramref name="key"/> を左（<paramref name="step"/> が負）か右へ 1 つ動かした配置（端なら変えない）。</summary>
    public static IReadOnlyList<TableColumnLayout> Shift(IReadOnlyList<TableColumnLayout> layout, string key, int step)
    {
        var from = IndexOf(layout, key);
        var to = from + Math.Sign(step);
        return from < 0 || to < 0 || to >= layout.Count ? layout : Move(layout, key, layout[to].Key);
    }

    /// <summary>列 <paramref name="key"/> の幅を変えた配置（<see cref="MinColumnWidth"/> より狭くしない。幅は整数にそろえる）。</summary>
    public static IReadOnlyList<TableColumnLayout> Resize(IReadOnlyList<TableColumnLayout> layout, string key, double width) =>
        [.. layout.Select(c => c.Key == key ? c with { Width = Math.Round(Math.Max(MinColumnWidth, width)) } : c)];

    /// <summary>
    /// 列の今の幅（右端の列は幅を指定せず、残りの幅いっぱいに伸ばす。null）。HTML の表（table-layout: fixed）は、幅を指定しない列に残りを配るため、
    /// 右端の列だけを指定しない（desktop-tables.md の 10.5 の「右端の列は残りの幅いっぱい」）。
    /// </summary>
    public static double? WidthOf(IReadOnlyList<TableColumnLayout> layout, int index, double fillMinWidth) =>
        index == layout.Count - 1 ? null : layout[index].Width > 0 ? layout[index].Width : fillMinWidth;

    /// <summary>右端の列の最小の幅（保存した幅。無ければ初期の最小の幅）。</summary>
    public static double LastMinWidth(IReadOnlyList<TableColumnLayout> layout, double fillMinWidth) =>
        layout.Count == 0 ? 0 : layout[^1].Width > 0 ? layout[^1].Width : fillMinWidth;

    /// <summary>表の最小の幅（これより狭いウィンドウでは、表の区画だけを横にスクロールする。列は縮めない）。</summary>
    public static double MinTableWidth(IReadOnlyList<TableColumnLayout> layout, double fillMinWidth) =>
        CheckColumnWidth + layout.Select((_, i) => WidthOf(layout, i, fillMinWidth) ?? 0).Sum() + LastMinWidth(layout, fillMinWidth);

    /// <summary>左上のチェックボックスの状態（true: 今のページの行をすべて選んだ、false: どれも選んでいない、null: 一部）。</summary>
    public static bool? AllState(int pageRows, int selectedOnPage) =>
        pageRows > 0 && selectedOnPage == pageRows ? true : selectedOnPage == 0 ? false : null;

    private static int IndexOf(IReadOnlyList<TableColumnLayout> layout, string key)
    {
        for (var i = 0; i < layout.Count; i++)
        {
            if (layout[i].Key == key)
                return i;
        }
        return -1;
    }
}
