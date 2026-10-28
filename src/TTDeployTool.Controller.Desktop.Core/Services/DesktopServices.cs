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
/// <param name="Page">押されたときに開く画面（<see cref="ViewModels.Pages"/> の名前。無ければジョブか、ジョブの一覧）</param>
public sealed record AppNotice(string Title, string Message, string? JobId = null, string? Page = null)
{
    /// <summary>通知の引数の、開くジョブの ID のキー。</summary>
    public const string JobArgument = "job";

    /// <summary>通知の引数の、開く画面の名前のキー。</summary>
    public const string PageArgument = "page";
}

/// <summary>Windows の通知（OS の機能を使うため、画面の側で実装する。詳細設計 desktop-jobs.md の 5）。</summary>
public interface INotificationService
{
    void Show(AppNotice notice);

    /// <summary>Windows の設定で、このアプリの通知が許可されているか（docs/design/desktop/desktop-jobs.md の 6.6）。</summary>
    NotificationPermission Permission { get; }
}

/// <summary>Windows のサインイン時の起動の状態（<c>Windows.ApplicationModel.StartupTaskState</c> と、使えない場合）。</summary>
public enum StartupTaskStatus
{
    Disabled,
    Enabled,

    /// <summary>タスク マネージャーや「設定」のスタートアップで無効にされた（アプリからは有効にできない）。</summary>
    DisabledByUser,

    /// <summary>グループ ポリシーで無効、またはこの PC では使えない。</summary>
    DisabledByPolicy,

    /// <summary>パッケージで動いていないなど、確かめられない。</summary>
    Unavailable,
}

/// <summary>Windows の StartupTaskState の読み替え（画面の側の StartupTaskService が使う。WinRT の型を使わずに単体テストで確かめるため、名前で受け取る）。</summary>
public static class StartupTaskStates
{
    /// <summary><c>Windows.ApplicationModel.StartupTaskState</c> の名前から読み替える（ポリシーで有効なものは有効、知らない名前は無効）。</summary>
    public static StartupTaskStatus FromWindowsState(string name) => name switch
    {
        "Enabled" or "EnabledByPolicy" => StartupTaskStatus.Enabled,
        "DisabledByUser" => StartupTaskStatus.DisabledByUser,
        "DisabledByPolicy" => StartupTaskStatus.DisabledByPolicy,
        _ => StartupTaskStatus.Disabled,
    };
}

/// <summary>Windows のサインイン時の起動（MSIX の StartupTask。画面の側で実装する。docs/design/desktop/desktop-jobs.md の 6.5）。</summary>
public interface IStartupTaskService
{
    Task<StartupTaskStatus> GetStatusAsync();

    /// <summary>有効にする（パッケージのデスクトップ アプリでは同意のダイアログは出ない）。結果の状態を返す。</summary>
    Task<StartupTaskStatus> EnableAsync();

    Task DisableAsync();
}

/// <summary>フォルダーや URL を OS の既定のアプリで開く。</summary>
public interface ISystemLauncher
{
    void OpenFolder(string path);

    void OpenUri(Uri uri);
}

/// <summary>
/// クリップボード（OS の機能を使うため、画面の側で実装する）。パスワードのコピーに使うため、クリップボードの履歴とほかの PC への同期に残さない。
/// </summary>
public interface IClipboardService
{
    void CopyText(string text);
}

/// <summary>資格情報から Azure を読む・操作するものを作る（テストで差し替える）。</summary>
public interface IAzureClients
{
    ITenantDirectory Tenants(TokenCredential credential);

    ISubscriptionDirectory Subscriptions(TokenCredential credential);

    IAzureDeploymentGateway Gateway(IDeploymentLogger logger, TokenCredential credential);

    /// <summary>本ツールで作ったリソースを、複数のサブスクリプションからまとめて読む（デプロイの一覧）。</summary>
    IDeploymentDirectory Deployments(TokenCredential credential);
}

/// <summary>Azure の管理 API を、IPv6 と IPv4 を並行して試す通信（<see cref="HappyEyeballs"/>）で使う。</summary>
public sealed class AzureClients : IAzureClients
{
    public ITenantDirectory Tenants(TokenCredential credential) => new AzureTenantDirectory(HappyEyeballs.Arm(credential));

    public ISubscriptionDirectory Subscriptions(TokenCredential credential) => new AzureSubscriptionDirectory(HappyEyeballs.Arm(credential));

    public IAzureDeploymentGateway Gateway(IDeploymentLogger logger, TokenCredential credential) => new AzureSdkExecutor(logger, credential);

    public IDeploymentDirectory Deployments(TokenCredential credential) => new ResourceGraphDeploymentDirectory(HappyEyeballs.Arm(credential));
}
