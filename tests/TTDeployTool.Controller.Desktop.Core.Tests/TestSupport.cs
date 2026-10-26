using System.ComponentModel;
using Azure.Core;
using Azure.ResourceManager.AppContainers;
using Moq;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Domain.Azure;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>サインインの代わり（対話しない）。</summary>
internal sealed class FakeSignIn : ISignInService
{
    public static readonly SignedInAccount Alice = new("alice@example.com", "home");

    public TokenCredential CredentialValue { get; } = Mock.Of<TokenCredential>();

    public SignedInAccount? Restored { get; set; }

    public Exception? RestoreError { get; set; }

    public Exception? SignInError { get; set; }

    public Exception? SignOutError { get; set; }

    public int SignOuts { get; private set; }

    /// <summary>復元・サインインを、これが終わるまで待たせる（処理中の表示を確かめる）。</summary>
    public Task Gate { get; set; } = Task.CompletedTask;

    public async Task<SignedInAccount?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await Gate;
        return RestoreError != null ? throw RestoreError : Restored;
    }

    public async Task<SignedInAccount> SignInAsync(CancellationToken cancellationToken = default)
    {
        await Gate;
        return SignInError != null ? throw SignInError : Alice;
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        SignOuts++;
        return SignOutError != null ? Task.FromException(SignOutError) : Task.CompletedTask;
    }

    public TokenCredential Credential => CredentialValue;
}

internal sealed class FakeDialogs : IDialogService
{
    public string? Folder { get; set; }

    public bool Confirm { get; set; }

    public List<string> Confirmations { get; } = [];

    public List<string> Messages { get; } = [];

    public Task<string?> PickFolderAsync() => Task.FromResult(Folder);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add(title);
        Messages.Add(message);
        return Task.FromResult(Confirm);
    }
}

internal sealed class FakeNotifications : INotificationService
{
    public List<AppNotice> Shown { get; } = [];

    public void Show(AppNotice notice) => Shown.Add(notice);
}

internal sealed class FakeStartupTask : IStartupTaskService
{
    /// <summary>今の状態（既定は使えない。尋ねるダイアログをほかのテストに出さないため）。</summary>
    public StartupTaskStatus Status { get; set; } = StartupTaskStatus.Unavailable;

    /// <summary>有効にしたときの結果（既定は有効）。</summary>
    public StartupTaskStatus EnableResult { get; set; } = StartupTaskStatus.Enabled;

    public Task<StartupTaskStatus> GetStatusAsync() => Task.FromResult(Status);

    public Task<StartupTaskStatus> EnableAsync()
    {
        Status = EnableResult;
        return Task.FromResult(Status);
    }

    public Task DisableAsync()
    {
        Status = StartupTaskStatus.Disabled;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public List<string> Copied { get; } = [];

    public void CopyText(string text) => Copied.Add(text);
}

internal sealed class FakeLauncher : ISystemLauncher
{
    public List<string> Opened { get; } = [];

    public void OpenFolder(string path) => Opened.Add(path);

    public void OpenUri(Uri uri) => Opened.Add(uri.ToString());
}

/// <summary>
/// Azure の代わり。テナント・サブスクリプション・リソースの状態をテストで決める。
/// 作成・削除は <see cref="Release"/> が終わるまで止まる（実行中の状態を確かめるため）。
/// </summary>
internal sealed class FakeAzure : IAzureClients
{
    public const string EnvId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/demo-env";

    public Mock<IAzureDeploymentGateway> GatewayMock { get; } = new();
    public List<TenantInfo> TenantList { get; } = [new("t1", "開発", "dev.example.com")];
    public Dictionary<string, List<SubscriptionInfo>> SubscriptionsByTenant { get; } = new() { ["t1"] = [new("sub", "検証", "Enabled", "t1")] };
    public Dictionary<string, Exception> TenantErrors { get; } = [];
    public Exception? TenantsError { get; set; }
    public List<ContainerAppInfo> Apps { get; } = [];
    public List<ContainerAppDetails> AppDetails { get; } = [];
    public List<PermissionSet> Permissions { get; } = [new(["*"], [])];
    public Exception? PermissionsError { get; set; }
    public ResourceGroupInfo? ResourceGroup { get; set; } = new("rg", "japaneast");
    public ManagedEnvironmentInfo? Environment { get; set; }
    public Task Release { get; set; } = Task.CompletedTask;
    public string? DeployedUrl { get; set; } = "https://demo-tt.example.io";
    public List<TokenCredential> Credentials { get; } = [];
    public bool HasStorageAccount { get; set; }

    /// <summary>リソースグループの中のリソースの一覧（削除しても変わらない。テストで入れ替える）。</summary>
    public List<AzureResourceInfo> Resources { get; } = [];

    /// <summary>DB のデータを置くストレージアカウントの名前（サブスクリプション・リソースグループ・アプリ名から決まる）。</summary>
    public static string StorageAccountName => ResourceNaming.StorageAccount("sub", "rg", "demo");

    public FakeAzure()
    {
        var env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
        GatewayMock.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => ResourceGroup);
        GatewayMock.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).ReturnsAsync(() => Environment);
        GatewayMock.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => Apps.ToList());
        GatewayMock.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => AppDetails.ToList());
        GatewayMock.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .Returns(() => PermissionsError != null ? Task.FromException<IReadOnlyList<PermissionSet>>(PermissionsError) : Task.FromResult<IReadOnlyList<PermissionSet>>(Permissions));
        GatewayMock.Setup(g => g.GetResourcePermissionsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        GatewayMock.Setup(g => g.GetStorageAccountAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string name, CancellationToken _) => HasStorageAccount ? new StorageAccountInfo(name, "japaneast", Managed) : null);
        GatewayMock.Setup(g => g.IsStorageAccountNameAvailableAsync("sub", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        GatewayMock.Setup(g => g.EnsureDbStorageAsync("sub", "rg", "demo", "japaneast", env, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        GatewayMock.Setup(g => g.DeleteStorageAccountAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        GatewayMock.Setup(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>())).Returns(() => Release);
        GatewayMock.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(env);
        GatewayMock.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(), env, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", DeployedUrl));
        GatewayMock.Setup(g => g.DeleteContainerAppAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Release;
                return true;
            });
        GatewayMock.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        GatewayMock.Setup(g => g.ListResourcesAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => Resources.ToList());
        GatewayMock.Setup(g => g.DeleteResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    public static IReadOnlyDictionary<string, string> Managed => ManagedTags.For("demo");

    /// <summary>本ツールで作った 3 つの Container App と Environment がある。</summary>
    public void Deployed(bool persisted = false)
    {
        foreach (var name in new[] { "demo-tt", "demo-redis" })
            Apps.Add(new ContainerAppInfo(name, EnvId, Managed));
        Apps.Add(new ContainerAppInfo("demo-db", EnvId, Managed, persisted ? [DbPersistence.StorageName] : null));
        HasStorageAccount = persisted;
        Environment = new ManagedEnvironmentInfo(EnvId, "demo-env", "japaneast", Managed, "Succeeded");
    }

    public ITenantDirectory Tenants(TokenCredential credential)
    {
        var tenants = new Mock<ITenantDirectory>();
        tenants.Setup(t => t.ListAsync(It.IsAny<CancellationToken>())).Returns(() =>
            TenantsError != null ? Task.FromException<IReadOnlyList<TenantInfo>>(TenantsError) : Task.FromResult<IReadOnlyList<TenantInfo>>(TenantList));
        return tenants.Object;
    }

    public ISubscriptionDirectory Subscriptions(TokenCredential credential)
    {
        var tenantId = ((TenantScopedCredential)credential).TenantId;
        var subscriptions = new Mock<ISubscriptionDirectory>();
        subscriptions.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).Returns(() =>
            TenantErrors.TryGetValue(tenantId, out var error)
                ? Task.FromException<IReadOnlyList<SubscriptionInfo>>(error)
                : Task.FromResult<IReadOnlyList<SubscriptionInfo>>(SubscriptionsByTenant.GetValueOrDefault(tenantId) ?? []));
        return subscriptions.Object;
    }

    public IAzureDeploymentGateway Gateway(TTDeployTool.Common.IDeploymentLogger logger, TokenCredential credential)
    {
        Credentials.Add(credential);
        return GatewayMock.Object;
    }

    /// <summary>テナントごとの、Resource Graph の結果（無いテナントは空）。</summary>
    public Dictionary<string, List<ManagedResourceRecord>> RecordsByTenant { get; } = [];

    /// <summary>テナントごとの、Resource Graph の失敗。</summary>
    public Dictionary<string, Exception> DirectoryErrors { get; } = [];

    /// <summary>Resource Graph の問い合わせを、これが終わるまで待たせる（確認が重ならないことを確かめる）。</summary>
    public Task DirectoryRelease { get; set; } = Task.CompletedTask;

    /// <summary>Resource Graph に問い合わせたテナントとサブスクリプション。</summary>
    public List<(string? TenantId, IReadOnlyList<string> Subscriptions)> DirectoryQueries { get; } = [];

    public IDeploymentDirectory Deployments(TokenCredential credential)
    {
        var tenantId = (credential as TenantScopedCredential)?.TenantId;
        var directory = new Mock<IDeploymentDirectory>();
        directory.Setup(d => d.ListAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<string>, CancellationToken>(async (subs, _) =>
            {
                DirectoryQueries.Add((tenantId, subs));
                await DirectoryRelease;
                return tenantId != null && DirectoryErrors.TryGetValue(tenantId, out var error)
                    ? throw error
                    : tenantId != null && RecordsByTenant.TryGetValue(tenantId, out var records) ? records.ToList() : [];
            });
        return directory.Object;
    }
}

/// <summary>プロパティの変更の通知を記録する。</summary>
internal sealed class ChangeRecorder
{
    public ChangeRecorder(INotifyPropertyChanged source) => source.PropertyChanged += (_, e) => Names.Add(e.PropertyName!);

    public List<string> Names { get; } = [];
}
