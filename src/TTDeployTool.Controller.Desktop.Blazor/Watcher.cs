using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;

namespace TTDeployTool.Controller.Desktop.Blazor;

/// <summary>
/// Core の ViewModel の変更（<see cref="INotifyPropertyChanged"/>・<see cref="INotifyCollectionChanged"/>・コマンドの押せるかの変更）を受けて、画面に描き直しを頼む
/// （docs/design/desktop/desktop-blazor-hybrid.md の 5、Q3 の A）。WinUI の x:Bind の代わり。Core は変えない。
/// 表の行のように入れ替わるものは <see cref="Sync"/> で、今の行だけを購読する。<see cref="Dispose"/> ですべての購読を外す。
/// </summary>
public sealed class Watcher(Action changed) : IDisposable
{
    private readonly List<object> _sources = [];
    private readonly HashSet<object> _rows = new(ReferenceEqualityComparer.Instance);

    /// <summary>変更を受け取る相手を足す（どの通知も持たないものは何もしない）。</summary>
    public void Watch(object source)
    {
        Subscribe(source);
        _sources.Add(source);
    }

    /// <summary>今の行だけを購読する（前の行の購読は外す）。</summary>
    public void Sync(IEnumerable<object> rows)
    {
        var now = new HashSet<object>(rows, ReferenceEqualityComparer.Instance);
        foreach (var row in _rows.ToList())
        {
            if (now.Contains(row))
                continue;
            Unsubscribe(row);
            _rows.Remove(row);
        }
        foreach (var row in now)
        {
            if (_rows.Add(row))
                Subscribe(row);
        }
    }

    /// <summary>購読している行の数（テストで確かめる）。</summary>
    public int RowCount => _rows.Count;

    public void Dispose()
    {
        foreach (var source in _sources.Concat(_rows))
            Unsubscribe(source);
        _sources.Clear();
        _rows.Clear();
    }

    private void Subscribe(object source)
    {
        if (source is INotifyPropertyChanged properties)
            properties.PropertyChanged += OnPropertyChanged;
        if (source is INotifyCollectionChanged collection)
            collection.CollectionChanged += OnCollectionChanged;
        if (source is ICommand command)
            command.CanExecuteChanged += OnCanExecuteChanged;
    }

    private void Unsubscribe(object source)
    {
        if (source is INotifyPropertyChanged properties)
            properties.PropertyChanged -= OnPropertyChanged;
        if (source is INotifyCollectionChanged collection)
            collection.CollectionChanged -= OnCollectionChanged;
        if (source is ICommand command)
            command.CanExecuteChanged -= OnCanExecuteChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e) => changed();

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => changed();

    private void OnCanExecuteChanged(object? sender, EventArgs e) => changed();
}
