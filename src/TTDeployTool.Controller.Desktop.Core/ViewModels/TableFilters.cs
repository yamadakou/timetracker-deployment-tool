using TTDeployTool.Controller.Desktop.Core.Mvvm;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 表の列（docs/design/desktop/desktop-tables.md の 10.4）。<see cref="ValueOf"/> は見出しの ▽ に出す短い値（空にしない）、
/// <see cref="TextOf"/> は表の上の文字のフィルタで探す文字（セルに見えている文字。既定は値）。日付の列は <see cref="DateOf"/> を持ち、日付のフィルタの対象になる。
/// </summary>
public sealed class TableColumn<T>(string key, string header, Func<T, string> value, Func<T, string?>? text = null, Func<T, DateTimeOffset?>? date = null)
{
    /// <summary>列の名前（画面の列の Tag・保存する列の配置の鍵）。</summary>
    public string Key => key;

    public string Header => header;

    public bool IsDate => date != null;

    public string ValueOf(T item) => value(item);

    public string TextOf(T item) => text == null ? value(item) : text(item) ?? "";

    public DateTimeOffset? DateOf(T item) => date!(item);

    public override string ToString() => header;
}

/// <summary>見出しの ▽ に出す値（<see cref="Count"/> はほかの絞り込みに合う行の数、<see cref="IsSelected"/> は今選んでいるか）。</summary>
public sealed record FilterValue(string Value, int Count, bool IsSelected);

/// <summary>表の上の文字のフィルタの対象（<see cref="Column"/> が null なら、すべての列）。</summary>
public sealed record TextFilterTarget<T>(TableColumn<T>? Column, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 表の絞り込み（docs/design/desktop/desktop-tables.md の 10.4）: 見出しの ▽（列ごとに、選んだ値のどれかに合う行。列どうしは「かつ」）と、
/// 表の上の文字のフィルタ（対象の列＋含む文字）・日付のフィルタ（対象の列＋いつから・いつまで）。表の行はページに分けるため、▽ の値も、
/// ページによらず、読んだすべての行（<paramref name="source"/>）から作る。
/// </summary>
public sealed class TableFilters<T> : ObservableObject
{
    private readonly Func<IEnumerable<T>> _source;
    private readonly IEqualityComparer<string> _comparer;
    private readonly Dictionary<string, HashSet<string>> _values = [];
    private TextFilterTarget<T> _textTarget;
    private string _text = "";
    private TableColumn<T> _dateColumn;

    /// <param name="comparer">▽ の値を束ねる比べ方（グループ名は大文字と小文字を区別しない）。既定は区別する</param>
    /// <param name="zone">日付のフィルタで日付を比べるタイムゾーン（既定はこの PC のタイムゾーン）</param>
    public TableFilters(IReadOnlyList<TableColumn<T>> columns, Func<IEnumerable<T>> source, IEqualityComparer<string>? comparer = null, TimeZoneInfo? zone = null)
    {
        Dates = new DateRangeFilter(zone);
        Columns = columns;
        _source = source;
        _comparer = comparer ?? StringComparer.Ordinal;
        TextTargets = [new TextFilterTarget<T>(null, "すべての列"), .. columns.Where(c => !c.IsDate).Select(c => new TextFilterTarget<T>(c, c.Header))];
        _textTarget = TextTargets[0];
        DateColumns = [.. columns.Where(c => c.IsDate)];
        _dateColumn = DateColumns[0];
        Dates.Changed += Raise;
        ClearCommand = new AsyncCommand(() =>
        {
            Clear();
            return Task.CompletedTask;
        }, () => IsActive);
    }

    public IReadOnlyList<TableColumn<T>> Columns { get; }

    /// <summary>文字のフィルタの対象（「すべての列」と、日付でない列）。</summary>
    public IReadOnlyList<TextFilterTarget<T>> TextTargets { get; }

    public TextFilterTarget<T> TextTarget
    {
        get => _textTarget;
        set
        {
            if (SetProperty(ref _textTarget, value) && Text.Length > 0)
                Raise();
        }
    }

    /// <summary>含む文字（前後の空白を除く。大文字と小文字は区別しない）。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value.Trim()))
                Raise();
        }
    }

    /// <summary>日付のフィルタの対象（日付の列）。</summary>
    public IReadOnlyList<TableColumn<T>> DateColumns { get; }

    public TableColumn<T> DateColumn
    {
        get => _dateColumn;
        set
        {
            if (SetProperty(ref _dateColumn, value) && Dates.IsActive)
                Raise();
        }
    }

    /// <summary>日付のフィルタのいつから・いつまで（その日を含む）。</summary>
    public DateRangeFilter Dates { get; }

    /// <summary>どれかの絞り込みをしているか。</summary>
    public bool IsActive => _values.Count > 0 || Text.Length > 0 || Dates.IsActive;

    /// <summary>絞り込みをすべて外す（▽ と、表の上の 2 つのフィルタ）。</summary>
    public AsyncCommand ClearCommand { get; }

    /// <summary>絞り込みが変わったとき（表を作り直す）。</summary>
    public event Action? Changed;

    public TableColumn<T> Column(string key) => Columns.First(c => c.Key == key);

    /// <summary>その列を ▽ で絞り込んでいるか。</summary>
    public bool IsFiltered(string key) => _values.ContainsKey(key);

    /// <summary>
    /// ▽ に出す値（ほかの列の絞り込みと表の上のフィルタに合う行の値。値の文字の順（PC の言語の設定によらない）。<paramref name="search"/> があれば、それを含む値だけ）。
    /// </summary>
    public IReadOnlyList<FilterValue> ValuesOf(string key, string? search = null)
    {
        var column = Column(key);
        _values.TryGetValue(key, out var selected);
        return [.. _source().Where(item => Matches(item, except: key))
            .GroupBy(column.ValueOf, _comparer)
            .Where(g => string.IsNullOrEmpty(search) || g.Key.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new FilterValue(g.Key, g.Count(), selected == null || selected.Contains(g.Key)))];
    }

    /// <summary>▽ で選んだ値に絞り込む（値がひとつも無ければ、どの行も出さない）。</summary>
    public void SetValues(string key, IEnumerable<string> values)
    {
        _values[key] = new HashSet<string>(values, _comparer);
        Raise();
    }

    /// <summary>その列の ▽ の絞り込みを外す（<paramref name="key"/> が null なら、すべての列）。</summary>
    public void ClearValues(string? key)
    {
        if (key == null ? _values.Count == 0 : !_values.Remove(key))
            return;
        if (key == null)
            _values.Clear();
        Raise();
    }

    /// <summary>行がすべての絞り込みに合うか。</summary>
    public bool Matches(T item) => Matches(item, except: null);

    /// <summary>
    /// 行を、列 <paramref name="keys"/> の順で、セルに見えている文字（<see cref="TableColumn{T}.TextOf"/>）のタブ区切りにする（クリップボードへのコピー）。
    /// 表計算に貼れるよう、タブ・改行・引用符を含む値は引用符で囲む（引用符は 2 つ重ねる）。
    /// </summary>
    public string CopyText(IEnumerable<T> items, IEnumerable<string> keys, bool headers)
    {
        var columns = keys.Select(Column).ToList();
        var lines = items.Select(item => string.Join('\t', columns.Select(c => Quote(c.TextOf(item)))));
        if (headers)
            lines = lines.Prepend(string.Join('\t', columns.Select(c => Quote(c.Header))));
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// 行を、列 <paramref name="keys"/> の順で、Markdown の表（1 行目が見出し）にする（クリップボードへのコピー。設定の Markdown のコピーと同じ書き方:
    /// セルの「|」と改行は、表が崩れないように置き換える）。
    /// </summary>
    public string CopyMarkdown(IEnumerable<T> items, IEnumerable<string> keys)
    {
        var columns = keys.Select(Column).ToList();
        return MarkdownText.Table([.. columns.Select(c => c.Header)], items.Select(item => (IReadOnlyList<string?>)[.. columns.Select(c => c.TextOf(item))]));
    }

    private static string Quote(string value) =>
        value.IndexOfAny(['\t', '\r', '\n', '"']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    private bool Matches(T item, string? except) =>
        _values.All(v => v.Key == except || v.Value.Contains(Column(v.Key).ValueOf(item)))
        && (Text.Length == 0 || (TextTarget.Column is { } c
            ? c.TextOf(item).Contains(Text, StringComparison.OrdinalIgnoreCase)
            : Columns.Where(x => !x.IsDate).Any(x => x.TextOf(item).Contains(Text, StringComparison.OrdinalIgnoreCase))))
        && Dates.Matches(DateColumn.DateOf(item));

    // 「絞り込みをすべて外す」は、絞り込んでいるときだけ押せる
    private void Clear()
    {
        _values.Clear();
        _text = "";
        OnPropertyChanged(nameof(Text));
        Dates.Changed -= Raise;
        Dates.From = null;
        Dates.To = null;
        Dates.Changed += Raise;
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsActive));
        ClearCommand.RaiseCanExecuteChanged();
        Changed?.Invoke();
    }
}
