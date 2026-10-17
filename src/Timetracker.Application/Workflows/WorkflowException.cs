using Timetracker.Application.Jobs;

namespace Timetracker.Application.Workflows;

/// <summary>計画を実行しなかった理由。利用者に見せる文は、フロントエンドが自分の言葉（ツールの名前やボタンの名前）で作る。</summary>
public enum WorkflowFailure
{
    /// <summary>計画が無い（使用済み・ほかの呼び出し元のもの・アプリの再起動で消えた）。</summary>
    PlanNotFound,

    /// <summary>計画の有効期限が切れた。</summary>
    PlanExpired,

    /// <summary>DB のデータが失われることへの同意が無い（計画は使用済みにしない）。</summary>
    DataLossNotAcknowledged,

    /// <summary>確認のアプリ名が計画と一致しない（計画は使用済みにしない）。</summary>
    AppNameMismatch,

    /// <summary>操作してよい範囲に無い（<see cref="WorkflowException.Blockers"/> に理由）。</summary>
    OutOfScope,

    /// <summary>実行の直前の確認で、実行できない状態になっていた（<see cref="WorkflowException.Blockers"/> に理由）。</summary>
    Blocked,

    /// <summary>計画の後に Azure の状態が変わった。</summary>
    Changed,

    /// <summary>同じ対象（<see cref="WorkflowException.Target"/>）のジョブが実行中（<see cref="WorkflowException.ConflictingJobId"/>）。</summary>
    Conflict,
}

/// <summary>計画 → 確認 → 実行の安全策で、実行しなかった場合の例外。</summary>
public sealed class WorkflowException : Exception
{
    public WorkflowException(
        WorkflowFailure failure, IReadOnlyList<string>? blockers = null, JobTarget? target = null, string? conflictingJobId = null)
        : base(failure.ToString())
    {
        Failure = failure;
        Blockers = blockers ?? [];
        Target = target;
        ConflictingJobId = conflictingJobId;
    }

    public WorkflowFailure Failure { get; }

    public IReadOnlyList<string> Blockers { get; }

    public JobTarget? Target { get; }

    public string? ConflictingJobId { get; }
}

/// <summary>保存する計画と、フロントエンドが計画に付ける情報（MCP では計画を作った方式。実行はその方式で行う）。</summary>
public sealed record StoredPlan<TPlan>(TPlan Plan, string? Context) where TPlan : class;
