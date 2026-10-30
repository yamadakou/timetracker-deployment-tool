namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 表（WinUI.TableView）の画面の側が使う決まり（docs/design/desktop/desktop-tables.md の 10.6・10.7）。画面の側（TableBinder）には
/// WinUI の呼び出しだけを残し、判断はここで行って単体テストで確かめる。表の行（ItemsSource の要素）は object で受け、
/// <c>itemOf</c> で Core の絞り込みの行に戻す。行でない値（使い回しの途中の行の入れ物が持つ「切り離された行」の目印など）には null が返る。
/// </summary>
public sealed class TableRows<T>(TableFilters<T> filters, Func<object, T?> itemOf)
    where T : class
{
    /// <summary>
    /// セルに見えている文字（列 <paramref name="key"/>）。行でない値と、文字を作れないときは空（表の読み上げ・コピー・並べ替えが行の配置の途中にも呼び、
    /// 例外を出すと WinRT の境界を越えてアプリが落ちるため。Desktop System Test、run 41035872850）。
    /// </summary>
    public string CellText(string key, object? row)
    {
        try
        {
            return row != null && itemOf(row) is { } item ? filters.Column(key).TextOf(item) : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>行を、列 <paramref name="keys"/> の順でタブ区切りにする（行でない値は飛ばす。<see cref="TableFilters{T}.CopyText"/>）。</summary>
    public string CopyText(IEnumerable<object> rows, IEnumerable<string> keys, bool headers) =>
        filters.CopyText(Items(rows), keys, headers);

    /// <summary>行を、列 <paramref name="keys"/> の順で Markdown の表にする（行でない値は飛ばす。行が無ければ空）。</summary>
    public string CopyMarkdown(IEnumerable<object> rows, IEnumerable<string> keys)
    {
        var items = Items(rows);
        return items.Count == 0 ? "" : filters.CopyMarkdown(items, keys);
    }

    private List<T> Items(IEnumerable<object> rows) => [.. rows.Select(itemOf).OfType<T>()];
}

/// <summary>表の画面の側が使う、行の型によらない決まり（desktop-tables.md の 10.6・10.7）。</summary>
public static class TableRules
{
    /// <summary>縦のスクロールバーの幅（マウスを置いて広がったとき）より少し広く。</summary>
    public const double ScrollBarSpace = 16;

    /// <summary>TableView は、残りの幅いっぱいの列の幅を「表の幅 - 32」から求め、左のチェックボックスの列の幅を引かないため、その差も余白にする。</summary>
    public const double HeaderReserve = 32;

    /// <summary>
    /// 余白の列の幅（チェックボックスの列の幅 <paramref name="cellsOffset"/> から）。変えなくてよいときは null: 幅が NaN・無限のとき
    /// （行を減らした直後など、表は一時的に NaN にする。NaN から幅を作ると例外になり、アプリが落ちた。Desktop System Test、run 41050364091）と、
    /// 今の幅 <paramref name="current"/> と同じとき。
    /// </summary>
    public static double? SpacerWidth(double cellsOffset, double? current)
    {
        if (!double.IsFinite(cellsOffset))
            return null;
        var width = ScrollBarSpace + Math.Max(0, cellsOffset - HeaderReserve);
        return width == current ? null : width;
    }

    /// <summary>右クリックのメニュー・コピーの対象の行: 選んだ行。選んでいなければ、右クリックした行（それも無ければ無し）。</summary>
    public static IReadOnlyList<object> Target(IReadOnlyList<object> selected, object? clicked) =>
        selected.Count > 0 ? selected : clicked != null ? [clicked] : [];

    /// <summary>
    /// 表の内蔵の並べ替えが決めた向き（列ごと。null は並べ替えていない）から、Core の並べ替えを決める。並べ替えている列が無ければ null（既定の順に戻す）。
    /// </summary>
    public static (string Key, bool Descending)? SortRequest(IEnumerable<(string Key, bool? Descending)> columns) =>
        columns.FirstOrDefault(c => c.Descending != null) is { Descending: { } descending } sorted ? (sorted.Key, descending) : null;

    /// <summary>
    /// 列の順 <paramref name="current"/>（余白の列は <paramref name="spacer"/>）を、保存した順 <paramref name="order"/> に並べ、余白の列を最後にする動かし方
    /// （前から順に当てる <c>Columns.Move(From, To)</c>）。保存した順に無い列は、その後ろに今の順で残る。
    /// </summary>
    public static IReadOnlyList<(int From, int To)> Arrange(IReadOnlyList<string> current, IReadOnlyList<string> order, string spacer)
    {
        var columns = current.ToList();
        var moves = new List<(int From, int To)>();
        void Move(int from, int to)
        {
            // 余白の列の無い表は動かさない
            if (from < 0 || from == to)
                return;
            var key = columns[from];
            columns.RemoveAt(from);
            columns.Insert(to, key);
            moves.Add((from, to));
        }
        // 保存した順にあって表に無い列（版の違い）は飛ばす
        var present = order.Where(columns.Contains).ToList();
        for (var i = 0; i < present.Count; i++)
            Move(columns.IndexOf(present[i]), i);
        Move(columns.IndexOf(spacer), columns.Count - 1);
        return moves;
    }

    /// <summary>保存する列の幅（数の幅でなければ 0 = 残りの幅いっぱい）。</summary>
    public static TableColumnLayout Saved(string key, double? pixels) => new(key, pixels ?? 0);

    /// <summary>セルの文字のツールチップ（6 行で切れたときだけ全体を出す）。</summary>
    public static string? ToolTip(bool trimmed, string text) => trimmed ? text : null;

    /// <summary>
    /// 「絞り込みをすべて外す」などで Core の文字が変わったとき、入力欄に書き戻すか（前後の空白だけの違いは、入力中のため書き換えない）。
    /// </summary>
    public static bool WriteBack(string boxText, string filterText) => boxText.Trim() != filterText;
}

/// <summary>
/// 表の上の「含む文字」の入力の待ち合わせ（desktop-tables.md の 10.5）。打つたびに絞り込むと、日本語の変換の途中で一覧が作り直され、
/// 入力欄からフォーカスが外れることがあるため、変換の間は待ち、入力が <see cref="Delay"/> 止まってから絞り込む。
/// タイマーは画面の側（DispatcherQueueTimer）が持ち、<paramref name="restart"/> で掛け直し、切れたら <see cref="Elapsed"/> を呼ぶ。
/// </summary>
public sealed class TextFilterInput(Action restart, Action<string> apply)
{
    /// <summary>入力が止まってから絞り込むまでの時間。</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);

    private bool _composing;

    /// <summary>入力欄の文字が変わった（変換の途中を含む）。</summary>
    public void Changed() => restart();

    /// <summary>日本語などの変換が始まった。</summary>
    public void CompositionStarted() => _composing = true;

    /// <summary>変換が終わった（確定・取り消し）。</summary>
    public void CompositionEnded()
    {
        _composing = false;
        restart();
    }

    /// <summary>タイマーが切れた。変換の間は絞り込まない（変換が終わると掛け直す）。絞り込んだら true。</summary>
    public bool Elapsed(string text)
    {
        if (_composing)
            return false;
        apply(text);
        return true;
    }
}
