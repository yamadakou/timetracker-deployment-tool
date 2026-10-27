using TTDeployTool.Application.Inventory;

namespace TTDeployTool.Controller.Desktop.Core.ViewModels;

/// <summary>
/// デプロイの一覧の「状態」（3 つの Container App の Azure 上の状態をまとめたもの。No.73）。並べ替えはこの順。
/// </summary>
public enum DeploymentState
{
    /// <summary>3 つとも実行中。</summary>
    Running,

    /// <summary>どれかが停止中（Stopped・Suspended）。</summary>
    Stopped,

    /// <summary>どれかが作成・更新・削除の途中（provisioningState が InProgress・Deleting、runningStatus が Progressing）。</summary>
    Processing,

    /// <summary>どれかの作成・更新が失敗した・取り消された（provisioningState が Failed・Canceled）。</summary>
    Failed,

    /// <summary>3 つの Container App と Environment の一部だけがある。</summary>
    Partial,

    /// <summary>このアプリでデプロイしたが、Azure の一覧（Resource Graph）にまだ反映されていない（状態がまだ分からない）。</summary>
    Pending,

    /// <summary>上のどれにも当たらない。</summary>
    Unknown,
}

/// <summary>「状態」の絞り込みの選択肢（<see cref="State"/> が null なら、すべて）。</summary>
public sealed record DeploymentStateFilterOption(DeploymentState? State, string Label);

/// <summary>
/// 3 つの Container App の状態から、一覧の「状態」を決める（docs/design/desktop-jobs.md の 6.3、No.73）。値は Container Apps の REST API の
/// provisioningState（InProgress・Succeeded・Failed・Canceled・Deleting）と runningStatus（Progressing・Running・Stopped・Suspended・Ready）。
/// 悪いほうを優先する（失敗 → 処理中 → 停止中 → 実行中）。
/// </summary>
public static class DeploymentStates
{
    public static DeploymentState Of(DeploymentEntry entry)
    {
        if (!entry.IsComplete)
            return DeploymentState.Partial;
        AppEntry[] apps = [entry.Timetracker!, entry.Db!, entry.Redis!];
        bool Any(Func<AppEntry, string?> value, params string[] states) =>
            apps.Any(a => states.Contains(value(a), StringComparer.OrdinalIgnoreCase));
        if (apps.All(a => a.ProvisioningState == null && a.RunningStatus == null))
            return DeploymentState.Pending;
        if (Any(a => a.ProvisioningState, "Failed", "Canceled"))
            return DeploymentState.Failed;
        if (Any(a => a.ProvisioningState, "InProgress", "Deleting") || Any(a => a.RunningStatus, "Progressing"))
            return DeploymentState.Processing;
        if (Any(a => a.RunningStatus, "Stopped", "Suspended"))
            return DeploymentState.Stopped;
        return apps.All(a => string.Equals(a.RunningStatus, "Running", StringComparison.OrdinalIgnoreCase)) ? DeploymentState.Running : DeploymentState.Unknown;
    }

    public static string Label(DeploymentState state) => state switch
    {
        DeploymentState.Running => "実行中",
        DeploymentState.Stopped => "停止中",
        DeploymentState.Processing => "処理中（作成・更新・削除）",
        DeploymentState.Failed => "失敗",
        DeploymentState.Partial => "一部だけ",
        DeploymentState.Pending => "デプロイ済み（反映待ち）",
        _ => "不明",
    };

    /// <summary>絞り込みの選択肢（「すべて」と、それぞれの状態）。</summary>
    public static readonly IReadOnlyList<DeploymentStateFilterOption> Filters =
    [
        new(null, "すべて"),
        .. Enum.GetValues<DeploymentState>().Select(s => new DeploymentStateFilterOption(s, Label(s))),
    ];
}
