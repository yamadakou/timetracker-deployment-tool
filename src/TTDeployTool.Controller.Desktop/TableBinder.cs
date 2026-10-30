using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Services;
using WinUI.TableView;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// 表（WinUI.TableView）と、Core の絞り込み・並べ替え・列の配置をつなぐ（docs/design/desktop/desktop-tables.md の 10）。列の Tag は Core の列の名前。
/// 表の行は今のページの行だけのため、▽ の値と絞り込みは Core（<see cref="TableFilters{T}"/>）で行い、並べ替えも Core の決まりで行う
/// （表の内蔵の並べ替えは、向きを読んだら外す）。列の幅と順序は、変えるたびに保存する。
/// 表の上の「含む文字」は、入力が止まってから絞り込む（<see cref="BindText"/>）。
/// 表の最後の列（Tag が空）は、見出しの無い余白の列で、右端の列の境目と文字を、縦のスクロールバーの左に出す（10.6）。
/// セルの文字のコピーは、Ctrl+C（選んだ行）と、セルの右クリックのメニュー（セルの文字・選んだ行・選んだ行の Markdown の表）で行う。
/// 判断（余白の幅・セルの文字・コピーの対象・並べ替え・列の配置・入力の待ち合わせ）は Core の <see cref="TableRows{T}"/>・<see cref="TableRules"/>・<see cref="TextFilterInput"/> で行い、
/// ここは WinUI の呼び出しだけにする（10.7）。
/// </summary>
internal sealed class TableBinder<T>
    where T : class
{
    private readonly TableView _table;
    private readonly TableFilters<T> _filters;
    private readonly TableLayout _layout;
    private readonly TableRows<T> _rows;
    private readonly Func<string?> _sortColumn;
    private readonly Func<bool> _sortDescending;
    private readonly Action<string, bool> _sort;
    private readonly Action _clearSort;
    private readonly TableViewColumn _spacer;
    private readonly ClipboardService _clipboard = new();
    private bool _applying;
    private bool _sortPending;
    private (string Key, object Row)? _menuCell;

    /// <param name="itemOf">表の行（ItemsSource の要素）から、Core の絞り込みの行を取り出す。行でない値（使い回しの途中の行の入れ物が持つ
    /// 「切り離された行」の目印など）には null を返す</param>
    /// <param name="canSort">見出しで並べ替えられる列か（並べ替えない列を押すと、Core の並べ替えが例外になる）</param>
    public TableBinder(TableView table, TableFilters<T> filters, TableLayout layout, INotifyPropertyChanged list, Func<object, T?> itemOf, Func<string, bool> canSort,
        Func<string?> sortColumn, Func<bool> sortDescending, Action<string, bool> sort, Action clearSort)
    {
        _table = table;
        _filters = filters;
        _layout = layout;
        _rows = new TableRows<T>(filters, itemOf);
        _sortColumn = sortColumn;
        _sortDescending = sortDescending;
        _sort = sort;
        _clearSort = clearSort;
        _spacer = table.Columns.Single(c => !IsData(c));
        _spacer.CanSort = false;
        _spacer.CanFilter = false;
        _spacer.CanResize = false;
        _spacer.CanReorder = false;
        _spacer.OperationContentBinding = TextBinding(_ => "");
        table.FilterHandler = new Handler(this);
        // 見出しを押す・▽ の「昇順」「降順」「並べ替えを解除」: 表が向きを決めた後に読み、Core で並べ替える
        table.Sorting += (_, _) => SortLater();
        table.ClearSorting += (_, _) => SortLater();
        table.CopyToClipboard += OnCopyToClipboard;
        table.CellContextFlyout = CreateMenu();
        table.CellContextFlyoutOpening += (_, e) => _menuCell = IsData(table.Columns.VisibleColumns[e.Slot.Column]) ? (Key(table.Columns.VisibleColumns[e.Slot.Column]), e.Item) : null;
        // チェックボックスの列の幅は、表が行の配置の途中で設定する。その中で列の幅を変えると、配置を入れ子で計算し直してアプリが落ちる
        // （記録の残らない落ち方。Desktop System Test、run 41020015841）ため、配置が終わってから変える
        table.RegisterPropertyChangedCallback(TableView.CellsHorizontalOffsetProperty, (_, _) => table.DispatcherQueue.TryEnqueue(SizeSpacer));
        filters.Changed += ShowFiltered;
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "SortColumn" or "SortDescending")
                ShowSort();
        };
        table.ColumnReordered += (_, _) => KeepSpacerLast();
        foreach (var column in DataColumns)
        {
            var key = Key(column);
            // テンプレートの列は、初期値では並べ替えも ▽ もできない（TableViewTemplateColumn が CanSort・CanFilter を false にする）
            column.CanSort = canSort(key);
            column.CanFilter = true;
            // 列の値（コピー・読み上げ・表の内蔵の並べ替え）は、セルに見えている文字。無いと行そのもの（型の名前）になる
            column.OperationContentBinding = TextBinding(row => _rows.CellText(key, row));
            column.RegisterPropertyChangedCallback(TableViewColumn.WidthProperty, (_, _) => Save());
        }
        Apply(layout.Current);
        SizeSpacer();
        ShowSort();
    }

    /// <summary>列の幅と順序を初期値に戻す。</summary>
    public void Reset() => Apply(_layout.Reset());

    /// <summary>
    /// 「含む文字」の入力欄を、表の上の文字のフィルタにつなぐ。打つたびに絞り込むと、日本語の変換の途中で一覧が作り直され、
    /// 入力欄からフォーカスが外れることがあるため、変換の間は待ち、入力が止まってから絞り込む。絞り込みで入力欄からフォーカスが外れたら戻す。
    /// </summary>
    public void BindText(TextBox box)
    {
        var timer = _table.DispatcherQueue.CreateTimer();
        timer.Interval = TextFilterInput.Delay;
        timer.IsRepeating = false;
        var input = new TextFilterInput(timer.Start, text => _filters.Text = text);
        timer.Tick += (_, _) =>
        {
            var focused = box.FocusState != FocusState.Unfocused;
            var caret = box.SelectionStart;
            if (input.Elapsed(box.Text) && focused)
            {
                _table.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (box.FocusState == FocusState.Unfocused)
                    {
                        box.Focus(FocusState.Programmatic);
                        box.SelectionStart = Math.Min(caret, box.Text.Length);
                    }
                });
            }
        };
        box.Text = _filters.Text;
        box.TextChanged += (_, _) => input.Changed();
        box.TextCompositionStarted += (_, _) => input.CompositionStarted();
        box.TextCompositionEnded += (_, _) => input.CompositionEnded();
        // 「絞り込みをすべて外す」などで文字が変わったら、入力欄に出す
        _filters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TableFilters<T>.Text) && TableRules.WriteBack(box.Text, _filters.Text))
                box.Text = _filters.Text;
        };
    }

    private static string Key(TableViewColumn column) => (string)column.Tag;

    private static bool IsData(TableViewColumn column) => column.Tag is string { Length: > 0 };

    private IEnumerable<TableViewColumn> DataColumns => _table.Columns.Where(IsData);

    private static Binding TextBinding(Func<object, string> text) => new() { Converter = new TextConverter(text) };

    // 保存した配置を表に当てる（幅 0 は残りの幅いっぱい）。余白の列は最後のまま
    private void Apply(IReadOnlyList<TableColumnLayout> layout)
    {
        _applying = true;
        try
        {
            Arrange([.. layout.Select(c => c.Key)]);
            foreach (var saved in layout)
            {
                if (DataColumns.FirstOrDefault(c => Key(c) == saved.Key) is { } column)
                    column.Width = saved.Width > 0 ? new GridLength(saved.Width) : new GridLength(1, GridUnitType.Star);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private void Save()
    {
        if (!_applying)
            _layout.Save([.. DataColumns.Select(c => TableRules.Saved(Key(c), c.Width.IsAbsolute ? c.Width.Value : null))]);
    }

    // 列を順 order に並べ、余白の列を最後にする（Core の TableRules.Arrange が動かし方を決める）
    private void Arrange(IReadOnlyList<string> order)
    {
        foreach (var (from, to) in TableRules.Arrange([.. _table.Columns.Select(c => IsData(c) ? Key(c) : "")], order, ""))
            _table.Columns.Move(from, to);
    }

    // 余白の列は動かせないが、ほかの列をその右に置けるため、順序を変えるたびに最後に戻す
    private void KeepSpacerLast()
    {
        Arrange([.. DataColumns.Select(Key)]);
        Save();
    }

    // 行を減らした直後など、表はチェックボックスの列の幅を一時的に NaN にする。NaN のときと同じ幅のときは変えない（Core の TableRules.SpacerWidth）
    private void SizeSpacer()
    {
        if (TableRules.SpacerWidth(_table.CellsHorizontalOffset, _spacer.Width.IsAbsolute ? _spacer.Width.Value : null) is { } width)
            _spacer.Width = new GridLength(width);
    }

    private void SortLater()
    {
        if (_sortPending)
            return;
        _sortPending = true;
        _table.DispatcherQueue.TryEnqueue(() =>
        {
            _sortPending = false;
            var request = TableRules.SortRequest(DataColumns.Select(c => (Key(c), c.SortDirection is { } d ? d == SortDirection.Descending : (bool?)null)));
            // 表の内蔵の並べ替えは外し、Core が並べた順のまま出す
            _table.SortDescriptions.Clear();
            if (request is { } r)
                _sort(r.Key, r.Descending);
            else
                _clearSort();
            ShowSort();
        });
    }

    // 見出しの矢印を Core の並べ替えに合わせる
    private void ShowSort()
    {
        var key = _sortColumn();
        var direction = _sortDescending() ? SortDirection.Descending : SortDirection.Ascending;
        foreach (var column in DataColumns)
            column.SortDirection = Key(column) == key ? direction : null;
    }

    // ▽ の絞り込みの印を Core の絞り込みに合わせる（「絞り込みをすべて外す」の後など）
    private void ShowFiltered()
    {
        foreach (var column in DataColumns)
            column.IsFiltered = _filters.IsFiltered(Key(column));
    }

    // Ctrl+C（Shift も押すと見出しつき）: 選んだ行を、今の列の順でタブ区切りにする（余白の列は入れない）。行を選んでいなければ、表の内蔵のコピー（選んだセル）
    private void OnCopyToClipboard(object? sender, TableViewCopyToClipboardEventArgs e)
    {
        if (_table.SelectedItems.Count == 0)
            return;
        e.Handled = true;
        CopyRows(_table.SelectedItems, e.IncludeHeaders);
    }

    private void CopyRows(IEnumerable<object> rows, bool headers) => Copy(_rows.CopyText(rows, VisibleKeys, headers));

    private IEnumerable<string> VisibleKeys => DataColumns.Where(c => c.Visibility == Visibility.Visible).Select(Key);

    // 選んだ行（選んでいなければ、右クリックした行）
    private IReadOnlyList<object> MenuRows => TableRules.Target([.. _table.SelectedItems], _menuCell?.Row);

    // 右クリックしたセルの文字と、選んだ行（選んでいなければ、右クリックした行）
    private MenuFlyout CreateMenu()
    {
        var cell = new MenuFlyoutItem { Text = "この文字をコピー" };
        cell.Click += (_, _) =>
        {
            if (_menuCell is { } c)
                Copy(_rows.CellText(c.Key, c.Row));
        };
        var rows = new MenuFlyoutItem { Text = "選んだ行をコピー" };
        rows.Click += (_, _) => CopyRows(MenuRows, headers: false);
        // Markdown の表（見出しつき。設定の Markdown のコピーと同じ書き方）
        var markdown = new MenuFlyoutItem { Text = "選んだ行を Markdown 形式でコピー" };
        markdown.Click += (_, _) => Copy(_rows.CopyMarkdown(MenuRows, VisibleKeys));
        var menu = new MenuFlyout { Items = { cell, rows, markdown } };
        menu.Opening += (_, _) => cell.IsEnabled = _menuCell != null;
        return menu;
    }

    private void Copy(string text)
    {
        if (text.Length == 0)
            return;
        // クリップボードがほかのアプリに使われていると開けないことがある（CLIPBRD_E_CANT_OPEN）。その 1 回はあきらめる
        try
        {
            _clipboard.CopyText(text);
        }
        catch (Exception)
        {
        }
    }

    private sealed class TextConverter(Func<object, string> text) : IValueConverter
    {
        // 表の読み上げ（UI Automation）・コピー・並べ替えが、行の配置の途中にも呼ぶ。例外は出さない（Core の TableRows.CellText）
        public object? Convert(object value, Type targetType, object parameter, string language) => text(value);

        public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
    }

    // 表の ▽ の値の一覧と絞り込みを、Core で行う
    private sealed class Handler(TableBinder<T> owner) : IColumnFilterHandler
    {
        public IDictionary<TableViewColumn, ICollection<object?>> SelectedValues { get; } = new Dictionary<TableViewColumn, ICollection<object?>>();

        public IList<TableViewFilterItem> GetFilterItems(TableViewColumn column, string? searchText) =>
            [.. owner._filters.ValuesOf(Key(column), searchText).Select(v => new TableViewFilterItem(v.IsSelected, v.Value, v.Count, showCount: true))];

        public void ApplyFilter(TableViewColumn column) =>
            owner._filters.SetValues(Key(column), SelectedValues.TryGetValue(column, out var values) ? values.OfType<string>() : []);

        public void ClearFilter(TableViewColumn? column)
        {
            if (column == null)
                SelectedValues.Clear();
            else
                SelectedValues.Remove(column);
            owner._filters.ClearValues(column == null ? null : Key(column));
            owner.ShowFiltered();
        }

        // 行は Core が絞り込んで渡すため、表では絞り込まない
        public bool Filter(TableViewColumn column, object? item) => true;
    }
}
