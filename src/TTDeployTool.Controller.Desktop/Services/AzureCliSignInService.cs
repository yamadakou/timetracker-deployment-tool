#if UITEST_SIGNIN
using Azure.Core;
using Azure.Identity;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>
/// UI テストの System Test（docs/design/desktop/desktop-ui-tests.md の 7）のためのサインイン。Azure CLI のサインイン（ワークフローの
/// azure/login による OIDC）を使い、WAM の対話をしない。テスト用のビルド（/p:DefineConstants=UITEST_SIGNIN）でだけ入り、
/// Store に出すパッケージには入らない。秘密の情報はアプリに渡さず、保存もしない。
/// </summary>
internal sealed class AzureCliSignInService : ISignInService
{
    private const string ArmScope = "https://management.azure.com/.default";

    // テナントごとにサブスクリプションを読むため、サインインしたテナント以外のトークンも取れるようにする
    private readonly AzureCliCredential _credential = new(new AzureCliCredentialOptions { AdditionallyAllowedTenants = { "*" } });
    private bool _signedIn;

    public TokenCredential Credential => _signedIn ? _credential : throw new InvalidOperationException("サインインしていません。");

    public async Task<SignedInAccount?> RestoreAsync(CancellationToken cancellationToken = default) => await SignInAsync(cancellationToken);

    public async Task<SignedInAccount> SignInAsync(CancellationToken cancellationToken = default)
    {
        // トークンを取れることを確かめる（Azure CLI にサインインしていなければ、ここで失敗する）
        await _credential.GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken);
        _signedIn = true;
        return new SignedInAccount("Azure CLI（UI テスト）", null);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _signedIn = false;
        return Task.CompletedTask;
    }
}
#endif
