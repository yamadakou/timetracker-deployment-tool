using TTDeployTool.Controller.Desktop.Core.Mvvm;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// 表の日時の列の絞り込み（いつから・いつまで。どちらも日付で、その日を含む。docs/design/desktop/desktop-jobs.md の 6.8）。
/// どちらかを入れると、その列に日時の無い行は出さない。
/// </summary>
public sealed class DateRangeFilter : ObservableObject
{
    private DateTimeOffset? _from;
    private DateTimeOffset? _to;

    public DateRangeFilter()
    {
        ClearCommand = new AsyncCommand(() =>
        {
            From = null;
            To = null;
            return Task.CompletedTask;
        }, () => IsActive);
    }

    /// <summary>この日から（画面の日付の入力。時刻は使わない）。</summary>
    public DateTimeOffset? From
    {
        get => _from;
        set => Set(ref _from, value);
    }

    /// <summary>この日まで（画面の日付の入力。時刻は使わない）。</summary>
    public DateTimeOffset? To
    {
        get => _to;
        set => Set(ref _to, value);
    }

    /// <summary>絞り込んでいるか。</summary>
    public bool IsActive => _from != null || _to != null;

    /// <summary>絞り込みを外す。</summary>
    public AsyncCommand ClearCommand { get; }

    /// <summary>いつから・いつまでが変わったとき。</summary>
    public event Action? Changed;

    /// <summary>日時（UTC）が範囲に入るか（日付はこの PC のタイムゾーンで比べる）。</summary>
    public bool Matches(DateTimeOffset? value)
    {
        if (!IsActive)
            return true;
        if (value is not { } at)
            return false;
        var date = at.ToLocalTime().Date;
        return (_from is not { } from || from.Date <= date) && (_to is not { } to || date <= to.Date);
    }

    private void Set(ref DateTimeOffset? field, DateTimeOffset? value)
    {
        if (!SetProperty(ref field, value))
            return;
        OnPropertyChanged(nameof(IsActive));
        ClearCommand.RaiseCanExecuteChanged();
        Changed?.Invoke();
    }
}
