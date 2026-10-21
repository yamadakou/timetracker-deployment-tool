using Azure.Core;
using TTDeployTool.Common;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Desktop.Core.Services;

/// <summary>サインインしたアカウント。</summary>
public sealed record SignedInAccount(string UserName, string? HomeTenantId);

/// <summary>
/// Azure へのサインイン（デスクトップアプリでは WAM を使う対話サインイン。詳細設計 5.1）。
/// </summary>
public interface ISignInService
{
    /// <summary>前回のサインインの情報があれば、対話なしで復元する。無い・使えない場合は null。</summary>
    Task<SignedInAccount?> RestoreAsync(CancellationToken cancellationToken = default);

    /// <summary>対話でサインインする（キャンセル・失敗は例外）。</summary>
    Task<SignedInAccount> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>サインアウトする（次回は対話でサインインする）。</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>サインインしたアカウントの資格情報（テナントは要求ごとに決める）。サインインしていなければ InvalidOperationException。</summary>
    TokenCredential Credential { get; }
}

/// <summary>画面のダイアログ（OS の機能を使うため、画面の側で実装する）。</summary>
public interface IDialogService
{
    /// <summary>フォルダーを選ばせる。キャンセルした場合は null。</summary>
    Task<string?> PickFolderAsync();

    /// <summary>確認する（「はい」なら true）。</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
}

/// <summary>Windows の通知 1 件。<see cref="JobId"/> は、押したときに開くジョブ（無ければ、アプリを開くだけ）。</summary>
public sealed record AppNotice(string Title, string Message, string? JobId = null);

/// <summary>Windows の通知（OS の機能を使うため、画面の側で実装する。詳細設計 desktop-jobs.md の 5）。</summary>
public interface INotificationService
{
    void Show(AppNotice notice);
}

/// <summary>フォルダーや URL を OS の既定のアプリで開く。</summary>
public interface ISystemLauncher
{
    void OpenFolder(string path);

    void OpenUri(Uri uri);
}

/// <summary>資格情報から Azure を読む・操作するものを作る（テストで差し替える）。</summary>
public interface IAzureClients
{
    ITenantDirectory Tenants(TokenCredential credential);

    ISubscriptionDirectory Subscriptions(TokenCredential credential);

    IAzureDeploymentGateway Gateway(IDeploymentLogger logger, TokenCredential credential);
}

/// <summary>Azure の管理 API を、IPv6 と IPv4 を並行して試す通信（<see cref="HappyEyeballs"/>）で使う。</summary>
public sealed class AzureClients : IAzureClients
{
    public ITenantDirectory Tenants(TokenCredential credential) => new AzureTenantDirectory(HappyEyeballs.Arm(credential));

    public ISubscriptionDirectory Subscriptions(TokenCredential credential) => new AzureSubscriptionDirectory(HappyEyeballs.Arm(credential));

    public IAzureDeploymentGateway Gateway(IDeploymentLogger logger, TokenCredential credential) => new AzureSdkExecutor(logger, credential);
}
