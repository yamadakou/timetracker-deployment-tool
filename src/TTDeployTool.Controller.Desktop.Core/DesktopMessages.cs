using Azure;
using Azure.Identity;
using TTDeployTool.Application.Workflows;

namespace TTDeployTool.Controller.Desktop.Core;

/// <summary>画面に表示するメッセージ（MCP のツールの名前の代わりに、画面の操作で次の手順を案内する）。</summary>
public static class DesktopMessages
{
    public const string PlanNotFound = "計画が見つかりません（使用済みか、アプリを再起動しました）。「計画」からやり直してください。";
    public const string PlanExpired = "計画の有効期限（15 分）が切れました。「計画」からやり直してください。";
    public const string DataLossNotAcknowledged = "DB のデータが失われることへの同意にチェックを入れてください。";
    public const string AppNameMismatch = "入力したアプリ名が、削除の計画のアプリ名と一致しません。";
    public const string Changed = "計画の後に Azure の状態が変わりました（リソースが作成・削除された、管理タグが変わったなど）。「計画」からやり直してください。";
    public const string SignInAgain = "サインインし直してから、もう一度試してください。";

    public const string MfaRequired =
        "Azure の管理 API での作成・更新・削除には多要素認証（MFA）が必要です。サインインし直し、MFA を行ってから、「計画」からやり直してください。";

    public const string MfaRegistrationRequired =
        "Azure の管理 API には多要素認証（MFA）が必要ですが、MFA の方法（認証アプリなど）が登録されていません。" +
        "https://aka.ms/mysecurityinfo で登録してから、サインインし直してください。";

    public const string EnvFileWarning =
        ".env には DB パスワードと Timetracker のパスワードが平文で保存されます。共有やリポジトリへのコミットは避けてください。";

    // AADSTS50076 は MFA を行っていない、AADSTS50079 は MFA の方法を登録していない
    private const string MfaRequiredError = "AADSTS50076";
    private const string MfaRegistrationRequiredError = "AADSTS50079";

    /// <summary>計画 → 確認 → 実行で実行しなかった理由。</summary>
    public static string For(WorkflowException ex) => ex.Failure switch
    {
        WorkflowFailure.PlanExpired => PlanExpired,
        WorkflowFailure.DataLossNotAcknowledged => DataLossNotAcknowledged,
        WorkflowFailure.AppNameMismatch => AppNameMismatch,
        WorkflowFailure.OutOfScope or WorkflowFailure.Blocked =>
            "実行の直前の確認で、実行できない状態になっていました:\n- " + string.Join("\n- ", ex.Blockers),
        WorkflowFailure.Changed => Changed,
        WorkflowFailure.Conflict =>
            $"同じ対象（{ex.Target!.ResourceGroup} / {ex.Target.AppName}）のデプロイまたは削除が実行中です。完了してから、「計画」からやり直してください。",
        _ => PlanNotFound,
    };

    /// <summary>Azure の呼び出しの失敗（サインイン・Azure のエラー）。</summary>
    public static string ForAzure(Exception ex) => ex switch
    {
        WorkflowException workflow => For(workflow),
        AuthenticationRequiredException => $"サインインが必要です。{SignInAgain}",
        CredentialUnavailableException unavailable => $"サインインできませんでした。{SignInAgain}（{unavailable.Message}）",
        AuthenticationFailedException auth when auth.Message.Contains(MfaRegistrationRequiredError) => MfaRegistrationRequired,
        AuthenticationFailedException auth when auth.Message.Contains(MfaRequiredError) => MfaRequired,
        AuthenticationFailedException auth => $"Azure の認証に失敗しました。{SignInAgain}（{auth.Message}）",
        RequestFailedException request => $"Azure がエラーを返しました（{request.Status} {request.ErrorCode}）: {request.Message}",
        _ => $"エラーが発生しました: {ex.Message}",
    };
}
