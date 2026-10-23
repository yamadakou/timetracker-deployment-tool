using System.Globalization;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>利用可能期限の近さ（一覧の色）。</summary>
public enum ExpiryLevel
{
    /// <summary>期限なし・記録なし。</summary>
    None,

    Normal,

    /// <summary><see cref="DeploymentListViewModel.SoonDays"/> 日以内。</summary>
    Soon,

    Expired,
}

/// <summary>
/// デプロイの一覧の 1 行。パスワードは「パスワードを表示」を押したときだけ Azure から読み、この行にだけ置く（PC には保存しない）。
/// 一覧を読み直す・ページを変える・サインアウトすると、行ごと消える。
/// </summary>
public sealed class DeploymentRowViewModel : ObservableObject
{
    public const string Mask = "••••••••";

    private readonly DeploymentListViewModel _owner;
    private DeploymentEntry _entry;
    private DeploymentPasswords? _passwords;
    private bool _isPasswordRevealed;
    private string? _error;
    private string? _message;
    private bool _editNoExpiry;
    private DateTimeOffset _editExpiryDate;

    internal DeploymentRowViewModel(DeploymentEntry entry, DeploymentListViewModel owner)
    {
        _entry = entry;
        _owner = owner;
        _editNoExpiry = entry.ExpiryKind == ExpiryKind.None;
        _editExpiryDate = InitialExpiryDate(entry);
        OpenCommand = new AsyncCommand(() =>
        {
            _owner.Open(Url!);
            return Task.CompletedTask;
        }, () => Url != null);
        UseAsTargetCommand = new AsyncCommand(() => _owner.UseAsTargetAsync(_entry, "wizard"));
        CheckStatusCommand = new AsyncCommand(() => _owner.UseAsTargetAsync(_entry, "status"));
        DeleteCommand = new AsyncCommand(() => _owner.UseAsTargetAsync(_entry, "delete"));
        ShowPasswordsCommand = new AsyncCommand(() => Busy.RunAsync("パスワードを読んでいます", ReadPasswordsAsync), () => _entry.Timetracker != null);
        TogglePasswordsCommand = new AsyncCommand(() =>
        {
            IsPasswordRevealed = !IsPasswordRevealed;
            return Task.CompletedTask;
        }, () => HasPasswords);
        CopyDbPasswordCommand = new AsyncCommand(() => CopyAsync(_passwords?.DbPassword, "DB のパスワード"), () => _owner.CanCopy && _passwords?.DbPassword != null);
        CopyTrackerPasswordCommand = new AsyncCommand(
            () => CopyAsync(_passwords?.TrackerPassword, "Timetracker のパスワード"), () => _owner.CanCopy && _passwords?.TrackerPassword != null);
        ChangeExpiryCommand = new AsyncCommand(() => Busy.RunAsync("利用可能期限を変えています", ChangeExpiryAsync),
            () => _entry.Timetracker != null || _entry.Db != null || _entry.Redis != null);
    }

    public AsyncCommand OpenCommand { get; }

    /// <summary>対象に入れて、デプロイのウィザードを開く（再デプロイのため）。</summary>
    public AsyncCommand UseAsTargetCommand { get; }

    /// <summary>対象に入れて、実行状況の画面で読む。</summary>
    public AsyncCommand CheckStatusCommand { get; }

    /// <summary>対象に入れて、削除の画面を開き、計画を作る（アプリ名の入力 → 実行は、削除の画面で行う）。</summary>
    public AsyncCommand DeleteCommand { get; }

    public AsyncCommand ShowPasswordsCommand { get; }

    public AsyncCommand TogglePasswordsCommand { get; }

    public AsyncCommand CopyDbPasswordCommand { get; }

    public AsyncCommand CopyTrackerPasswordCommand { get; }

    public AsyncCommand ChangeExpiryCommand { get; }

    /// <summary>この行の操作の処理中の表示。</summary>
    public BusyState Busy { get; } = new();

    public DeploymentEntry Entry => _entry;

    public string Subscription => _owner.SubscriptionDisplay(_entry.SubscriptionId);

    public string ResourceGroup => _entry.ResourceGroup;

    public string AppName => _entry.AppName;

    public Uri? Url => _entry.Url is { } url ? new Uri(url) : null;

    public bool HasUrl => Url != null;

    public string DeployedText => _entry.DeployedAt is { } at ? Local(at).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "記録なし";

    public string ExpiryText => _entry.ExpiryKind switch
    {
        ExpiryKind.None => "期限なし",
        ExpiryKind.Date => Local(_entry.ExpiresAt!.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Level switch
        {
            ExpiryLevel.Expired => "（過ぎています）",
            ExpiryLevel.Soon => $"（{DeploymentListViewModel.SoonDays} 日以内）",
            _ => "",
        },
        _ => "記録なし",
    };

    /// <summary>リソースごとの期限のタグが食い違っている場合の注意（揃っていれば null）。</summary>
    public string? ExpiryWarning => _entry.ExpiryDiffers
        ? "リソースごとに利用可能期限のタグが違います（遅い方の期限で判断します。期限なしが 1 つでもあれば期限なし）。「期限を変える」で揃えてください。"
        : null;

    public bool HasExpiryWarning => ExpiryWarning != null;

    /// <summary>期限の近さ（画面の色）。</summary>
    public ExpiryLevel Level => _entry is { ExpiryKind: ExpiryKind.Date, ExpiresAt: { } at }
        ? at <= _owner.Time.GetUtcNow() ? ExpiryLevel.Expired
        : at <= _owner.Time.GetUtcNow().AddDays(DeploymentListViewModel.SoonDays) ? ExpiryLevel.Soon
        : ExpiryLevel.Normal
        : ExpiryLevel.None;

    public bool IsExpired => Level == ExpiryLevel.Expired;

    public bool IsExpirySoon => Level == ExpiryLevel.Soon;

    public string DbTypeText => _entry.DbType switch
    {
        "sqlserver" => "SQL Server",
        "postgresql" => "PostgreSQL",
        _ => "不明",
    };

    public string PersistenceText => _entry.DbPersisted
        ? "Azure Files に永続化"
        : _entry.StorageAccountName != null ? "永続化していない（以前の DB のデータが残っています）" : "永続化していない";

    public string SpecText => string.Join("、", new[] { ("Timetracker", _entry.Timetracker), ("DB", _entry.Db), ("Redis", _entry.Redis) }
        .Where(x => x.Item2 != null)
        .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Item1} {x.Item2!.Cpu?.ToString() ?? "?"} vCPU / {x.Item2.Memory ?? "?"}")));

    public string StateText => _entry.IsComplete
        ? _entry.Timetracker!.RunningStatus ?? _entry.Timetracker.ProvisioningState ?? "不明"
        : $"一部だけ（{string.Join("・", _entry.Missing)} がありません）";

    public bool HasPasswords => _passwords != null;

    public bool IsPasswordRevealed
    {
        get => _isPasswordRevealed;
        private set
        {
            if (SetProperty(ref _isPasswordRevealed, value, new Dependent(nameof(DbPasswordText))))
                OnPropertyChanged(nameof(TrackerPasswordText));
        }
    }

    public string? DbPasswordText => Shown(_passwords?.DbPassword);

    public string? TrackerPasswordText => Shown(_passwords?.TrackerPassword);

    /// <summary>期限の変更: 期限なしにするか（false なら <see cref="EditExpiryDate"/>）。</summary>
    public bool EditNoExpiry
    {
        get => _editNoExpiry;
        set => SetProperty(ref _editNoExpiry, value);
    }

    public DateTimeOffset EditExpiryDate
    {
        get => _editExpiryDate;
        set => SetProperty(ref _editExpiryDate, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value, new Dependent(nameof(HasError)));
    }

    public bool HasError => Error != null;

    /// <summary>操作の結果（コピーした・期限を変えた）。</summary>
    public string? Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    /// <summary>同じデプロイの新しい内容に入れ替える（期限の変更・ジョブの結果を重ねたとき。パスワードと操作の結果は残す）。</summary>
    internal void Update(DeploymentEntry entry)
    {
        if (entry == _entry)
            return;
        _entry = entry;
        foreach (var name in new[]
                 {
                     nameof(Entry), nameof(Url), nameof(HasUrl), nameof(DeployedText), nameof(ExpiryText), nameof(ExpiryWarning), nameof(HasExpiryWarning),
                     nameof(Level), nameof(IsExpired), nameof(IsExpirySoon),
                     nameof(DbTypeText), nameof(PersistenceText), nameof(SpecText), nameof(StateText),
                 })
            OnPropertyChanged(name);
        OpenCommand.RaiseCanExecuteChanged();
        ShowPasswordsCommand.RaiseCanExecuteChanged();
        ChangeExpiryCommand.RaiseCanExecuteChanged();
    }

    private DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _owner.Time.LocalTimeZone);

    private string? Shown(string? value) => _passwords == null ? null : value == null ? "（ありません）" : IsPasswordRevealed ? value : Mask;

    // 期限の入力の初期値（今の期限、無ければ今日から 32 日後）
    private DateTimeOffset InitialExpiryDate(DeploymentEntry entry)
    {
        var zone = _owner.Time.LocalTimeZone;
        var date = entry is { ExpiryKind: ExpiryKind.Date, ExpiresAt: { } at }
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime)
            : DeploymentExpiry.Today(_owner.Time.GetUtcNow(), zone).AddDays(DeploymentExpiry.DefaultDays);
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private async Task ReadPasswordsAsync()
    {
        Error = null;
        Message = null;
        try
        {
            var passwords = await _owner.GatewayFor(_entry.SubscriptionId, Busy).Maintenance(_owner.Time)
                .ReadPasswordsAsync(_entry.SubscriptionId, _entry.ResourceGroup, _entry.AppName);
            // 値は書かない
            _owner.Logger.LogInformation("パスワードを読みました: {Subscription} / {ResourceGroup} / {AppName}", _entry.SubscriptionId, _entry.ResourceGroup, _entry.AppName);
            _passwords = passwords;
            IsPasswordRevealed = false;
            OnPasswordsChanged();
        }
        catch (Exception ex)
        {
            Error = ex is InvalidOperationException ? ex.Message : DesktopMessages.ForAzure(ex);
        }
    }

    private void OnPasswordsChanged()
    {
        OnPropertyChanged(nameof(HasPasswords));
        OnPropertyChanged(nameof(DbPasswordText));
        OnPropertyChanged(nameof(TrackerPasswordText));
        TogglePasswordsCommand.RaiseCanExecuteChanged();
        CopyDbPasswordCommand.RaiseCanExecuteChanged();
        CopyTrackerPasswordCommand.RaiseCanExecuteChanged();
    }

    private Task CopyAsync(string? value, string name)
    {
        _owner.Copy(value!);
        Message = $"{name}をコピーしました（クリップボードの履歴には残しません）。";
        return Task.CompletedTask;
    }

    private async Task ChangeExpiryAsync()
    {
        Error = null;
        Message = null;
        var input = EditNoExpiry ? DeploymentExpiry.None : EditExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            var decision = await _owner.GatewayFor(_entry.SubscriptionId, Busy).Maintenance(_owner.Time)
                .ChangeExpiryAsync(_entry.SubscriptionId, _entry.ResourceGroup, _entry.AppName, input);
            _owner.Logger.LogInformation("利用可能期限を変えました: {Subscription} / {ResourceGroup} / {AppName} → {Expiry}",
                _entry.SubscriptionId, _entry.ResourceGroup, _entry.AppName, decision.TagValue);
            Message = $"利用可能期限を {decision.Describe(_owner.Time.LocalTimeZone)} にしました。";
            _owner.ExpiryChanged(_entry, decision);
        }
        catch (Exception ex)
        {
            Error = ex is ArgumentException or InvalidOperationException ? ex.Message : DesktopMessages.ForAzure(ex);
        }
    }
}
