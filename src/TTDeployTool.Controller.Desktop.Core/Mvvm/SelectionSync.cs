namespace TTDeployTool.Controller.Desktop.Core.Mvvm;

/// <summary>
/// 表（ListView）の選択と ViewModel の選択を互いに合わせる（ListView の SelectedItems は結び付けられないため。docs/design/desktop/desktop-wizard.md の 7）。
/// 画面の側は、表の選択の一覧を <see cref="Attach"/> で渡し、選択が変わったら <see cref="ViewChanged"/> を呼ぶだけにする。
/// ViewModel が選び直したとき（一覧を表示し直した後に保った選択・「選択を外す」）は、<see cref="ViewModelChanged"/> で表に戻す。
/// 戻している間の表の選択の変更は、ViewModel に返さない。
/// </summary>
public sealed class SelectionSync<T>
    where T : class
{
    private readonly Func<IReadOnlyList<T>> _selected;
    private readonly Action<IEnumerable<T>> _setSelection;
    private IList<object>? _view;
    private bool _syncing;

    /// <param name="selected">ViewModel の今の選択</param>
    /// <param name="setSelection">表で選んだ行を ViewModel に伝える</param>
    public SelectionSync(Func<IReadOnlyList<T>> selected, Action<IEnumerable<T>> setSelection)
    {
        _selected = selected;
        _setSelection = setSelection;
    }

    /// <summary>表の選択の一覧（ListView.SelectedItems）を渡す。</summary>
    public void Attach(IList<object> view) => _view = view;

    /// <summary>表の選択が変わった（ListView.SelectionChanged）。</summary>
    public void ViewChanged()
    {
        if (_view != null && !_syncing)
            _setSelection(_view.OfType<T>().ToList());
    }

    /// <summary>ViewModel の選択が変わった: 表の選択が違えば合わせる。</summary>
    public void ViewModelChanged()
    {
        var wanted = _selected();
        if (_view == null || _view.OfType<T>().SequenceEqual(wanted))
            return;
        _syncing = true;
        try
        {
            _view.Clear();
            foreach (var item in wanted)
                _view.Add(item);
        }
        finally
        {
            _syncing = false;
        }
    }
}
