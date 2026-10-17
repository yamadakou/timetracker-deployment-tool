using System.ComponentModel;
using Azure.Core;
using Azure.ResourceManager.AppContainers;
using Moq;
using Timetracker.Controller.Desktop.Core.Services;
using Timetracker.Domain.Azure;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Desktop.Core.Tests;

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

    public Task<SignedInAccount?> RestoreAsync(CancellationToken cancellationToken = default) =>
        RestoreError != null ? Task.FromException<SignedInAccount?>(RestoreError) : Task.FromResult(Restored);

    public Task<SignedInAccount> SignInAsync(CancellationToken cancellationToken = default) =>
        SignInError != null ? Task.FromException<SignedInAccount>(SignInError) : Task.FromResult(Alice);

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

    public Task<string?> PickFolderAsync() => Task.FromResult(Folder);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Confirmations.Add(title);
        return Task.FromResult(Confirm);
    }
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

    public FakeAzure()
    {
        var env = new Mock<ContainerAppManagedEnvironmentResource>().Object;
        GatewayMock.Setup(g => g.GetResourceGroupAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => ResourceGroup);
        GatewayMock.Setup(g => g.GetEnvironmentAsync("sub", "rg", "demo-env", It.IsAny<CancellationToken>())).ReturnsAsync(() => Environment);
        GatewayMock.Setup(g => g.ListContainerAppsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => Apps.ToList());
        GatewayMock.Setup(g => g.ListContainerAppDetailsAsync("sub", "rg", It.IsAny<CancellationToken>())).ReturnsAsync(() => AppDetails.ToList());
        GatewayMock.Setup(g => g.GetPermissionsAsync("sub", "rg", It.IsAny<CancellationToken>()))
            .Returns(() => PermissionsError != null ? Task.FromException<IReadOnlyList<PermissionSet>>(PermissionsError) : Task.FromResult<IReadOnlyList<PermissionSet>>(Permissions));
        GatewayMock.Setup(g => g.GetResourcePermissionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        GatewayMock.Setup(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>())).Returns(() => Release);
        GatewayMock.Setup(g => g.EnsureContainerAppEnvAsync("sub", "rg", "demo", "japaneast", It.IsAny<CancellationToken>())).ReturnsAsync(env);
        GatewayMock.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(), env, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", DeployedUrl));
        GatewayMock.Setup(g => g.DeleteContainerAppAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Release;
                return true;
            });
        GatewayMock.Setup(g => g.DeleteEnvironmentAsync("sub", "rg", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    public static IReadOnlyDictionary<string, string> Managed => ManagedTags.For("demo");

    /// <summary>本ツールで作った 3 つの Container App と Environment がある。</summary>
    public void Deployed()
    {
        foreach (var name in new[] { "demo-tt", "demo-redis", "demo-db" })
            Apps.Add(new ContainerAppInfo(name, EnvId, Managed));
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

    public IAzureDeploymentGateway Gateway(Timetracker.Common.IDeploymentLogger logger, TokenCredential credential)
    {
        Credentials.Add(credential);
        return GatewayMock.Object;
    }
}

/// <summary>プロパティの変更の通知を記録する。</summary>
internal sealed class ChangeRecorder
{
    public ChangeRecorder(INotifyPropertyChanged source) => source.PropertyChanged += (_, e) => Names.Add(e.PropertyName!);

    public List<string> Names { get; } = [];
}
