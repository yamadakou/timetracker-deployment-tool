using System.Collections.ObjectModel;
using Azure.Core;
using Microsoft.Extensions.Logging;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>選べるサブスクリプション（テナントごとに読む）。</summary>
public sealed record SubscriptionChoice(string SubscriptionId, string DisplayName, string? State, string? TenantId, string? TenantName)
{
    public string Display => TenantName == null ? $"{DisplayName}（{SubscriptionId}）" : $"{DisplayName}（{SubscriptionId}、{TenantName}）";
}

/// <summary>操作する対象（サブスクリプション・リソースグループ・アプリ名・リージョン）。</summary>
public sealed class TargetViewModel : ObservableObject
{
    private readonly ISignInService _signIn;
    private readonly IAzureClients _clients;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private SubscriptionChoice? _selected;
    private string _resourceGroup = "";
    private string _appName = Defaults.DefaultAppName;
    private string _location = Defaults.DefaultLocation;
    private string? _error;

    /// <param name="time">ジョブでトークンの取得を待つ上限を測る時刻（省略時はシステムの時刻）</param>
    public TargetViewModel(ISignInService signIn, IAzureClients clients, ILogger logger, TimeProvider? time = null)
    {
        _signIn = signIn;
        _clients = clients;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        LoadCommand = new AsyncCommand(LoadAsync);
    }

    /// <summary>対象が変わったとき（設定の検証をやり直す）。</summary>
    public event EventHandler? Changed;

    public AsyncCommand LoadCommand { get; }

    /// <summary>サブスクリプションの読み込みの処理中の表示。</summary>
    public BusyState Busy { get; } = new();

    public ObservableCollection<SubscriptionChoice> Subscriptions { get; } = [];

    /// <summary>読めなかったテナントなど、一覧についての注意。</summary>
    public ObservableCollection<string> Notices { get; } = [];

    public SubscriptionChoice? SelectedSubscription
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public string ResourceGroup
    {
        get => _resourceGroup;
        set => Set(ref _resourceGroup, value.Trim());
    }

    public string AppName
    {
        get => _appName;
        set => Set(ref _appName, value.Trim());
    }

    public string Location
    {
        get => _location;
        set => Set(ref _location, value.Trim());
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value, new Dependent(nameof(HasError)));
    }

    /// <summary>エラーを表示するか（画面の表示の切り替えに使う）。</summary>
    public bool HasError => Error != null;

    public string Subscription => SelectedSubscription?.SubscriptionId ?? "";

    /// <summary>選んだサブスクリプションの表示（「設定」の対象の欄）。</summary>
    public string SubscriptionDisplay => SelectedSubscription?.Display ?? "サブスクリプション: 「サインインと対象」で選んでください。";

    /// <summary>アプリ名の誤り（削除と状態の確認で、Azure を読む前に確かめる）。</summary>
    public string? TargetError =>
        SelectedSubscription == null ? "サブスクリプションを選んでください。"
        : ResourceGroup.Length == 0 ? "リソースグループ名を入力してください。"
        : AppNameValidator.GetError(AppName.ToLowerInvariant());

    /// <summary>テナントごとにサブスクリプションを読む。読めないテナントがあっても、ほかのテナントの一覧は続ける。</summary>
    public Task LoadAsync() => Busy.RunAsync("テナントの一覧を読んでいます", LoadCoreAsync);

    private async Task LoadCoreAsync()
    {
        Error = null;
        Notices.Clear();
        var previous = SelectedSubscription?.SubscriptionId;
        var choices = new List<SubscriptionChoice>();
        try
        {
            var credential = _signIn.Credential;
            var tenants = await _clients.Tenants(credential).ListAsync();
            _logger.LogInformation("テナント {Count} 件", tenants.Count);
            for (var i = 0; i < tenants.Count; i++)
            {
                var tenant = tenants[i];
                var name = tenant.DisplayName ?? tenant.DefaultDomain ?? tenant.TenantId;
                Busy.Report($"テナント {i + 1} / {tenants.Count}（{name}）のサブスクリプションを読んでいます");
                try
                {
                    var subscriptions = await _clients.Subscriptions(new TenantScopedCredential(credential, tenant.TenantId)).ListAsync();
                    choices.AddRange(subscriptions.Select(s => new SubscriptionChoice(s.SubscriptionId, s.DisplayName, s.State, tenant.TenantId, name)));
                    _logger.LogInformation("テナント {TenantId}: サブスクリプション {Count} 件", tenant.TenantId, subscriptions.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "テナント {TenantId} のサブスクリプションを読めませんでした", tenant.TenantId);
                    Notices.Add($"テナント {name} のサブスクリプションを読めませんでした（MFA などで、サインインし直すと読める場合があります）: {DesktopMessages.ForAzure(ex)}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "テナントの一覧を読めませんでした");
            Error = DesktopMessages.ForAzure(ex);
        }

        Subscriptions.Clear();
        foreach (var choice in choices.OrderBy(c => c.DisplayName, StringComparer.CurrentCulture))
            Subscriptions.Add(choice);
        SelectedSubscription = Subscriptions.FirstOrDefault(c => c.SubscriptionId == previous) ?? (Subscriptions.Count == 1 ? Subscriptions[0] : null);
    }

    /// <summary>サインアウトしたとき、一覧を消す。</summary>
    public void Clear()
    {
        Subscriptions.Clear();
        Notices.Clear();
        SelectedSubscription = null;
    }

    /// <summary>
    /// 選んだサブスクリプションのテナントで、サインインしたアカウントの権限で Azure を操作する接続。
    /// <paramref name="progress"/> を渡すと、Azure を読む前に段階を知らせる（計画・状態の確認の画面で使う。ジョブの中では渡さない）。
    /// </summary>
    public IAzureSession CreateSession(IProgress<string>? progress = null) => CreateSession(Credential(), progress);

    /// <summary>
    /// ジョブで使う接続。サインインの画面での対話が要ったまま、<see cref="TimeLimitedCredential.DefaultTimeout"/> を過ぎたら失敗させる
    /// （予定の時刻に誰もいない場合に、ジョブが止まったままにならないよう。詳細設計 desktop-jobs.md の 4.5）。
    /// </summary>
    public IAzureSession CreateJobSession() => CreateSession(new TimeLimitedCredential(Credential(), _time));

    /// <summary>
    /// 一覧から選んだサブスクリプションのテナントで操作する接続（選んでいる対象とは別のサブスクリプションの行の操作に使う）。
    /// </summary>
    public IAzureSession CreateSessionFor(string subscriptionId, IProgress<string>? progress = null) =>
        CreateSession(Credential(TenantOf(subscriptionId)), progress);

    /// <summary>一覧から選んだサブスクリプションのテナントで、ジョブに使う接続（期限での削除。<see cref="CreateJobSession"/> と同じく時間を区切る）。</summary>
    public IAzureSession CreateJobSessionFor(string subscriptionId) =>
        CreateSession(new TimeLimitedCredential(Credential(TenantOf(subscriptionId)), _time));

    /// <summary>テナントのサブスクリプションの、本ツールで作ったリソースを読むもの（デプロイの一覧）。</summary>
    public IDeploymentDirectory CreateDirectory(string? tenantId) => _clients.Deployments(Credential(tenantId));

    /// <summary>
    /// 対象を入れる（デプロイの一覧の「対象に入れる」）。サブスクリプションが一覧に無ければ入れずに false を返す。
    /// </summary>
    public bool Select(string subscriptionId, string resourceGroup, string appName)
    {
        var choice = Subscriptions.FirstOrDefault(c => string.Equals(c.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase));
        if (choice == null)
            return false;
        SelectedSubscription = choice;
        ResourceGroup = resourceGroup;
        AppName = appName;
        return true;
    }

    private string? TenantOf(string subscriptionId) =>
        Subscriptions.FirstOrDefault(c => string.Equals(c.SubscriptionId, subscriptionId, StringComparison.OrdinalIgnoreCase))?.TenantId;

    private TokenCredential Credential() => Credential(SelectedSubscription?.TenantId);

    private TokenCredential Credential(string? tenantId) => tenantId != null
        ? new TenantScopedCredential(_signIn.Credential, tenantId)
        : _signIn.Credential;

    private IAzureSession CreateSession(TokenCredential credential, IProgress<string>? progress = null)
    {
        return new UnrestrictedAzureSession(logger => progress == null
            ? _clients.Gateway(logger, credential)
            : ReportingGateway.Wrap(_clients.Gateway(logger, credential), progress));
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        if (!SetProperty(ref field, value, propertyName))
            return;
        OnPropertyChanged(nameof(Subscription));
        OnPropertyChanged(nameof(SubscriptionDisplay));
        OnPropertyChanged(nameof(TargetError));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
