using System.Collections.ObjectModel;
using Azure.Core;
using Timetracker.Application.Workflows;
using Timetracker.Controller.Desktop.Core.Mvvm;
using Timetracker.Controller.Desktop.Core.Services;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Desktop.Core.ViewModels;

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
    private SubscriptionChoice? _selected;
    private string _resourceGroup = "";
    private string _appName = Defaults.DefaultAppName;
    private string _location = Defaults.DefaultLocation;
    private string? _error;

    public TargetViewModel(ISignInService signIn, IAzureClients clients)
    {
        _signIn = signIn;
        _clients = clients;
        LoadCommand = new AsyncCommand(LoadAsync);
    }

    /// <summary>対象が変わったとき（設定の検証をやり直す）。</summary>
    public event EventHandler? Changed;

    public AsyncCommand LoadCommand { get; }

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
        private set => SetProperty(ref _error, value);
    }

    public string Subscription => SelectedSubscription?.SubscriptionId ?? "";

    /// <summary>アプリ名の誤り（削除と状態の確認で、Azure を読む前に確かめる）。</summary>
    public string? TargetError =>
        SelectedSubscription == null ? "サブスクリプションを選んでください。"
        : ResourceGroup.Length == 0 ? "リソースグループ名を入力してください。"
        : AppNameValidator.GetError(AppName.ToLowerInvariant());

    /// <summary>テナントごとにサブスクリプションを読む。読めないテナントがあっても、ほかのテナントの一覧は続ける。</summary>
    public async Task LoadAsync()
    {
        Error = null;
        Notices.Clear();
        var previous = SelectedSubscription?.SubscriptionId;
        var choices = new List<SubscriptionChoice>();
        try
        {
            var credential = _signIn.Credential;
            foreach (var tenant in await _clients.Tenants(credential).ListAsync())
            {
                var name = tenant.DisplayName ?? tenant.DefaultDomain ?? tenant.TenantId;
                try
                {
                    var subscriptions = await _clients.Subscriptions(new TenantScopedCredential(credential, tenant.TenantId)).ListAsync();
                    choices.AddRange(subscriptions.Select(s => new SubscriptionChoice(s.SubscriptionId, s.DisplayName, s.State, tenant.TenantId, name)));
                }
                catch (Exception ex)
                {
                    Notices.Add($"テナント {name} のサブスクリプションを読めませんでした（MFA などで、サインインし直すと読める場合があります）: {DesktopMessages.ForAzure(ex)}");
                }
            }
        }
        catch (Exception ex)
        {
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

    /// <summary>選んだサブスクリプションのテナントで、サインインしたアカウントの権限で Azure を操作する接続。</summary>
    public IAzureSession CreateSession()
    {
        TokenCredential credential = SelectedSubscription?.TenantId is { } tenantId
            ? new TenantScopedCredential(_signIn.Credential, tenantId)
            : _signIn.Credential;
        return new UnrestrictedAzureSession(logger => _clients.Gateway(logger, credential));
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        if (!SetProperty(ref field, value, propertyName))
            return;
        OnPropertyChanged(nameof(Subscription));
        OnPropertyChanged(nameof(TargetError));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
