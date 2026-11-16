using Azure;
using Azure.Identity;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;

namespace TTDeployTool.Controller.Desktop.Core;

/// <summary>画面に表示するメッセージ（MCP のツールの名前の代わりに、画面の操作で次の手順を案内する）。</summary>
public static class DesktopMessages
{
    // やり直し方は、メニューや画面にある名前で書く（「「計画」から」では何をすればよいか分からなかった。オーナーの指摘、2026-11-16）
    private const string PageRetry = "「計画し直す」を押し、内容を確かめてから実行してください。";
    private const string PlanNotFoundReason = "計画が見つかりません（アプリを起動し直したか、もう使われています）。";
    private const string PlanExpiredReason = "計画の有効期限（15 分）が切れました。";
    private const string ChangedReason = "計画の後に Azure の状態が変わりました（リソースが作成・削除された、管理タグが変わったなど）。";
    private const string MfaReason = "Azure の管理 API での作成・更新・削除には多要素認証（MFA）が必要です。サインインし直し、MFA を行ってから、";

    public const string PlanNotFound = PlanNotFoundReason + PageRetry;
    public const string PlanExpired = PlanExpiredReason + PageRetry;
    public const string DataLossNotAcknowledged = "DB のデータが失われることへの同意にチェックを入れてください。";
    public const string AppNameMismatch = "入力したアプリ名が、削除の計画のアプリ名と一致しません。";
    public const string Changed = ChangedReason + PageRetry;
    public const string SignInAgain = "サインインし直してから、もう一度試してください。";

    public const string MfaRequired = MfaReason + PageRetry;

    public const string MfaRegistrationRequired =
        "Azure の管理 API には多要素認証（MFA）が必要ですが、MFA の方法（認証アプリなど）が登録されていません。" +
        "https://aka.ms/mysecurityinfo で登録してから、サインインし直してください。";

    public const string EnvFileWarning =
        ".env には DB パスワードと TimeTracker のパスワードが平文で保存されます。共有やリポジトリへのコミットは避けてください。";

    // AADSTS50076 は MFA を行っていない、AADSTS50079 は MFA の方法を登録していない
    private const string MfaRequiredError = "AADSTS50076";
    private const string MfaRegistrationRequiredError = "AADSTS50079";

    /// <summary>
    /// やり直し方（<see cref="RetryHint.Page"/> は計画の画面の「計画し直す」、ジョブはメニューの操作）。
    /// </summary>
    public static string Retry(RetryHint hint) => hint switch
    {
        RetryHint.DeleteJob => "デプロイの一覧で、その行を右クリック →「Azure から削除…」で削除し直してください。",
        RetryHint.DeployJob => "ジョブの一覧で、このジョブを右クリック →「この設定で新しくデプロイ」でデプロイし直してください。",
        _ => PageRetry,
    };

    /// <summary>計画 → 確認 → 実行で実行しなかった理由と、やり直し方。</summary>
    public static string For(WorkflowException ex, RetryHint hint = RetryHint.Page) => ex.Failure switch
    {
        WorkflowFailure.PlanExpired => PlanExpiredReason + Retry(hint),
        WorkflowFailure.DataLossNotAcknowledged => DataLossNotAcknowledged,
        WorkflowFailure.AppNameMismatch => AppNameMismatch,
        WorkflowFailure.OutOfScope or WorkflowFailure.Blocked =>
            "実行の直前の確認で、実行できない状態になっていました:\n- " + string.Join("\n- ", DesktopTerms.ForScreen(ex.Blockers)),
        WorkflowFailure.Changed => ChangedReason + Retry(hint),
        WorkflowFailure.Conflict =>
            $"同じ対象（{(ex.Target!.AppName.Length == 0 ? $"{ex.Target.ResourceGroup}（リソースグループだけ）" : $"{ex.Target.ResourceGroup} / {ex.Target.AppName}")}）" +
            "のデプロイまたは削除が実行中です。完了してから、" + Retry(hint),
        _ => PlanNotFoundReason + Retry(hint),
    };

    /// <summary>Azure の呼び出しの失敗（サインイン・Azure のエラー）。</summary>
    /// <param name="hint">やり直し方（ジョブの失敗なら、ジョブの種類）</param>
    public static string ForAzure(Exception ex, RetryHint hint = RetryHint.Page) => ex switch
    {
        WorkflowException workflow => For(workflow, hint),
        DeletionSkippedException skipped => skipped.Message,
        AuthenticationRequiredException => $"サインインが必要です。{SignInAgain}",
        CredentialUnavailableException unavailable => $"サインインできませんでした。{SignInAgain}（{unavailable.Message}）",
        SignInTimeoutException timeout => $"{timeout.Message}{SignInAgain}",
        AuthenticationFailedException auth when auth.Message.Contains(MfaRegistrationRequiredError) => MfaRegistrationRequired,
        AuthenticationFailedException auth when auth.Message.Contains(MfaRequiredError) => MfaReason + Retry(hint),
        AuthenticationFailedException auth => $"Azure の認証に失敗しました。{SignInAgain}（{auth.Message}）",
        RequestFailedException request => $"Azure がエラーを返しました（{request.Status} {request.ErrorCode}）: {request.Message}",
        _ => $"エラーが発生しました: {ex.Message}",
    };
}

/// <summary>計画を使えなかったときの、やり直し方の案内の種類。</summary>
public enum RetryHint
{
    /// <summary>計画の画面（新しくデプロイの「確認と実行」・削除・まとめての削除）の「計画し直す」。</summary>
    Page,

    /// <summary>削除のジョブ: デプロイの一覧の「Azure から削除…」。</summary>
    DeleteJob,

    /// <summary>デプロイのジョブ: ジョブの一覧の「この設定で新しくデプロイ」。</summary>
    DeployJob,
}

