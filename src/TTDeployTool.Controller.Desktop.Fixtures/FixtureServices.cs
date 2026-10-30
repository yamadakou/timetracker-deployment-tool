using Azure.Core;
using TTDeployTool.Common;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Fixtures;

/// <summary>
/// UI テストのモックのサインイン（desktop-ui-tests.md の 6.2）。起動したときは復元しない（サインインしていない状態で始まる）。「サインイン」を押すと、
/// モックのデータのアカウントでサインインする（データが無ければ誤り）。資格情報は、本物の Azure に届かないよう、トークンを求められたら例外を出す。
/// </summary>
public sealed class FixtureSignInService(Func<FixtureData?> data) : ISignInService
{
    private bool _signedIn;

    public Task<SignedInAccount?> RestoreAsync(CancellationToken cancellationToken = default) => Task.FromResult<SignedInAccount?>(null);

    public Task<SignedInAccount> SignInAsync(CancellationToken cancellationToken = default)
    {
        var account = data()?.Account ?? throw new InvalidOperationException($"UI テストのモックのデータがありません（アプリのデータの {FixtureData.FileName}）。");
        _signedIn = true;
        return Task.FromResult(account);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _signedIn = false;
        return Task.CompletedTask;
    }

    public TokenCredential Credential => _signedIn ? new FixtureCredential() : throw new InvalidOperationException("サインインしていません。");
}

/// <summary>トークンを求められたら例外を出す資格情報（モックでは本物の Azure を呼ばない）。</summary>
public sealed class FixtureCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("UI テストのモックでは、Azure のトークンを発行しません。");

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("UI テストのモックでは、Azure のトークンを発行しません。");
}

/// <summary>UI テストのモックの Azure（テナント・サブスクリプション・デプロイの一覧・操作。desktop-ui-tests.md の 6.2）。</summary>
public sealed class FixtureAzureClients(FixtureData data) : IAzureClients
{
    public ITenantDirectory Tenants(TokenCredential credential) => new Directory<TenantInfo>(data.Tenants);

    public ISubscriptionDirectory Subscriptions(TokenCredential credential) => new Directory<SubscriptionInfo>(data.Subscriptions);

    public IAzureDeploymentGateway Gateway(IDeploymentLogger logger, TokenCredential credential) => new FixtureGateway(data);

    public IDeploymentDirectory Deployments(TokenCredential credential) => new DeploymentDirectory(data.Resources);

    private sealed class Directory<T>(IReadOnlyList<T> items) : ITenantDirectory, ISubscriptionDirectory
    {
        Task<IReadOnlyList<TenantInfo>> ITenantDirectory.ListAsync(CancellationToken cancellationToken) => Task.FromResult((IReadOnlyList<TenantInfo>)items);

        Task<IReadOnlyList<SubscriptionInfo>> ISubscriptionDirectory.ListAsync(CancellationToken cancellationToken) => Task.FromResult((IReadOnlyList<SubscriptionInfo>)items);
    }

    private sealed class DeploymentDirectory(IReadOnlyList<ManagedResourceRecord> resources) : IDeploymentDirectory
    {
        public Task<IReadOnlyList<ManagedResourceRecord>> ListAsync(IReadOnlyList<string> subscriptionIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedResourceRecord>>([.. resources.Where(r => subscriptionIds.Contains(r.SubscriptionId, StringComparer.OrdinalIgnoreCase))]);
    }
}
