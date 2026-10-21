using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Services;

/// <summary>
/// WAM（Windows の Web アカウント マネージャー）を使う対話サインイン（詳細設計 5.1）。WAM が使えない環境では、Azure.Identity が
/// ブラウザーでのサインインに切り替える。サインインの情報（AuthenticationRecord。アカウントとテナントの識別子で、秘密の情報を含まない）を
/// 保存し、次回の起動で対話なしに復元する。トークンのキャッシュは Azure.Identity が DPAPI で保護して保存する。
/// </summary>
internal sealed class WamSignInService : ISignInService
{
    // Azure の管理 API（ARM）のスコープ
    private const string ArmScope = "https://management.azure.com/.default";
    private const string CacheName = "ttdeploytool-desktop";

    private readonly IntPtr _hwnd;
    private readonly string? _clientId;
    private readonly string _recordPath;
    private InteractiveBrowserCredential? _credential;

    public WamSignInService(IntPtr hwnd, string? clientId, string dataDirectory)
    {
        _hwnd = hwnd;
        _clientId = clientId;
        _recordPath = Path.Combine(dataDirectory, "authentication-record.json");
    }

    public TokenCredential Credential => _credential ?? throw new InvalidOperationException("サインインしていません。");

    public async Task<SignedInAccount?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_recordPath))
            return null;
        AuthenticationRecord record;
        await using (var stream = File.OpenRead(_recordPath))
            record = await AuthenticationRecord.DeserializeAsync(stream, cancellationToken);

        // 対話なしでトークンを取れるかを確かめる（取れなければ、サインインしていない状態で始める）
        try
        {
            await Create(record, disableAutomaticAuthentication: true).GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken);
        }
        catch (AuthenticationRequiredException)
        {
            return null;
        }
        _credential = Create(record, disableAutomaticAuthentication: false);
        return ToAccount(record);
    }

    public async Task<SignedInAccount> SignInAsync(CancellationToken cancellationToken = default)
    {
        var credential = Create(null, disableAutomaticAuthentication: false);
        var record = await credential.AuthenticateAsync(new TokenRequestContext([ArmScope]), cancellationToken);
        await using (var stream = File.Create(_recordPath))
            await record.SerializeAsync(stream, cancellationToken);
        _credential = credential;
        return ToAccount(record);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _credential = null;
        File.Delete(_recordPath);
        return Task.CompletedTask;
    }

    // 作成・削除で MFA などの追加の認証（claims challenge）を求められた場合に WAM の画面を出せるよう、自動の対話は既定で許す（詳細設計 5.3）
    private InteractiveBrowserCredential Create(AuthenticationRecord? record, bool disableAutomaticAuthentication)
    {
        var options = new InteractiveBrowserCredentialBrokerOptions(_hwnd)
        {
            UseDefaultBrokerAccount = record == null,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = disableAutomaticAuthentication,
            // 公開の Azure（login.microsoftonline.com）だけを使うため、認証局のメタデータの取得（インスタンスの検出）をしない。
            // 実機の確認で、この取得がネットワークで応答せず、トークンを取るたびに 100 秒（HttpClient の既定の時間切れ）待っていた
            DisableInstanceDiscovery = true,
            // IPv6 の通信が応答なく捨てられる環境でも待たないよう、IPv6 と IPv4 を並行して試す（No.54）
            Transport = HappyEyeballs.Transport,
        };
        // テナントごとにサブスクリプションを読むため、アカウントが入れるすべてのテナントのトークンを取れるようにする
        options.AdditionallyAllowedTenants.Add("*");
        if (_clientId != null)
            options.ClientId = _clientId;
        return new InteractiveBrowserCredential(options);
    }

    private static SignedInAccount ToAccount(AuthenticationRecord record) => new(record.Username, record.TenantId);
}
