using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using WinUI.TableView;

namespace TTDeployTool.Controller.Desktop;

/// <summary>
/// 表（WinUI.TableView）と、Core の絞り込み・並べ替え・列の配置をつなぐ（docs/design/desktop/desktop-tables.md の 10）。列の Tag は Core の列の名前。
/// 表の行は今のページの行だけのため、▽ の値と絞り込みは Core（<see cref="TableFilters{T}"/>）で行い、並べ替えも Core の決まりで行う
/// （表の内蔵の並べ替えは、向きを読んだら外す）。列の幅と順序は、変えるたびに保存する。
/// 表の上の「含む文字」は、入力が止まってから絞り込む（<see cref="BindText"/>）。
/// </summary>
internal sealed class TableBinder<T>
{
    private readonly TableView _table;
    private readonly TableFilters<T> _filters;
    private readonly TableLayout _layout;
    private readonly Func<string?> _sortColumn;
    private readonly Func<bool> _sortDescending;
    private readonly Action<string, bool> _sort;
    private readonly Action _clearSort;
    private bool _applying;
    private bool _sortPending;

    /// <param name="sortMemberPath">行の型の文字列のプロパティ。表の内蔵の並べ替えが向きを読むまでの間に使う（無いと行そのものを比べて例外になる）</param>
    public TableBinder(TableView table, TableFilters<T> filters, TableLayout layout, INotifyPropertyChanged list, string sortMemberPath,
        Func<string?> sortColumn, Func<bool> sortDescending, Action<string, bool> sort, Action clearSort)
    {
        _table = table;
        _filters = filters;
        _layout = layout;
        _sortColumn = sortColumn;
        _sortDescending = sortDescending;
        _sort = sort;
        _clearSort = clearSort;
        table.FilterHandler = new Handler(this);
        // 見出しを押す・▽ の「昇順」「降順」「並べ替えを解除」: 表が向きを決めた後に読み、Core で並べ替える
        table.Sorting += (_, _) => SortLater();
        table.ClearSorting += (_, _) => SortLater();
        filters.Changed += ShowFiltered;
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "SortColumn" or "SortDescending")
                ShowSort();
        };
        table.ColumnReordered += (_, _) => Save();
        foreach (var column in table.Columns)
        {
            // テンプレートの列は、初期値では並べ替えも ▽ もできない（TableViewTemplateColumn が CanSort・CanFilter を false にする）
            column.CanSort = true;
            column.CanFilter = true;
            column.SortMemberPath = sortMemberPath;
            column.RegisterPropertyChangedCallback(TableViewColumn.WidthProperty, (_, _) => Save());
        }
        Apply(layout.Current);
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
        var composing = false;
        var timer = _table.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (composing)
                return;
            var focused = box.FocusState != FocusState.Unfocused;
            var caret = box.SelectionStart;
            _filters.Text = box.Text;
            if (focused)
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
        box.TextChanged += (_, _) => timer.Start();
        box.TextCompositionStarted += (_, _) => composing = true;
        box.TextCompositionEnded += (_, _) =>
        {
            composing = false;
            timer.Start();
        };
        // 「絞り込みをすべて外す」などで文字が変わったら、入力欄に出す（前後の空白だけの違いは、入力中のため書き換えない）
        _filters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TableFilters<T>.Text) && box.Text.Trim() != _filters.Text)
                box.Text = _filters.Text;
        };
    }

    private static string Key(TableViewColumn column) => (string)column.Tag;

    // 保存した配置を表に当てる（幅 0 は残りの幅いっぱい）
    private void Apply(IReadOnlyList<TableColumnLayout> layout)
    {
        _applying = true;
        try
        {
            for (var i = 0; i < layout.Count; i++)
            {
                var column = _table.Columns.First(c => Key(c) == layout[i].Key);
                var index = _table.Columns.IndexOf(column);
                if (index != i)
                    _table.Columns.Move(index, i);
                column.Width = layout[i].Width > 0 ? new GridLength(layout[i].Width) : new GridLength(1, GridUnitType.Star);
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
            _layout.Save([.. _table.Columns.Select(c => new TableColumnLayout(Key(c), c.Width.IsAbsolute ? c.Width.Value : 0))]);
    }

    private void SortLater()
    {
        if (_sortPending)
            return;
        _sortPending = true;
        _table.DispatcherQueue.TryEnqueue(() =>
        {
            _sortPending = false;
            var sorted = _table.Columns.FirstOrDefault(c => c.SortDirection != null);
            var descending = sorted?.SortDirection == SortDirection.Descending;
            // 表の内蔵の並べ替えは外し、Core が並べた順のまま出す
            _table.SortDescriptions.Clear();
            if (sorted == null)
                _clearSort();
            else
                _sort(Key(sorted), descending);
            ShowSort();
        });
    }

    // 見出しの矢印を Core の並べ替えに合わせる
    private void ShowSort()
    {
        var key = _sortColumn();
        var direction = _sortDescending() ? SortDirection.Descending : SortDirection.Ascending;
        foreach (var column in _table.Columns)
            column.SortDirection = Key(column) == key ? direction : null;
    }

    // ▽ の絞り込みの印を Core の絞り込みに合わせる（「絞り込みをすべて外す」の後など）
    private void ShowFiltered()
    {
        foreach (var column in _table.Columns)
            column.IsFiltered = _filters.IsFiltered(Key(column));
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
