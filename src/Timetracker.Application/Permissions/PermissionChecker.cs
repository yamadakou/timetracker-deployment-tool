using Timetracker.Domain.Azure;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Application.Permissions;

public enum PermissionCheckStatus
{
    /// <summary>必要な権限がすべてある。</summary>
    Ok,
    /// <summary>不足している権限がある。</summary>
    Missing,
    /// <summary>確認できなかった（理由は <see cref="PermissionCheckResult.Note"/>）。</summary>
    NotChecked,
}

/// <summary>権限の事前確認の結果。</summary>
public sealed record PermissionCheckResult(PermissionCheckStatus Status, IReadOnlyList<string> MissingActions, string? Note)
{
    public static PermissionCheckResult Ok { get; } = new(PermissionCheckStatus.Ok, [], null);

    public static PermissionCheckResult NotChecked(string note) => new(PermissionCheckStatus.NotChecked, [], note);
}

/// <summary>
/// 実行する ID がリソースグループで必要な権限を持っているかを、操作の前に確認する
/// （途中で権限エラーになり、一部のリソースだけが作成・削除された状態が残るのを防ぐ）。
/// 拒否割り当てや Azure Policy による拒否は反映されないため、確認を通っても実行時に失敗することはある。
/// </summary>
public sealed class PermissionChecker
{
    private readonly IAzureDeploymentGateway _gateway;

    public PermissionChecker(IAzureDeploymentGateway gateway)
    {
        _gateway = gateway;
    }

    public async Task<PermissionCheckResult> CheckAsync(
        string subscriptionId, string rgName, IReadOnlyCollection<string> actions, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PermissionSet> permissions;
        try
        {
            permissions = await _gateway.GetPermissionsAsync(subscriptionId, rgName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 確認できないことを理由に止めない（実際の操作で権限が無ければ Azure 側でエラーになる）
            return PermissionCheckResult.NotChecked($"権限の一覧を取得できませんでした: {ex.Message}");
        }

        var missing = PermissionEvaluator.Missing(permissions, actions);
        return missing.Count == 0
            ? PermissionCheckResult.Ok
            : new PermissionCheckResult(PermissionCheckStatus.Missing, missing, null);
    }
}
