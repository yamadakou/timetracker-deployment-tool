using Azure.ResourceManager;

namespace Timetracker.Infrastructure.Azure;

/// <summary>実行している ID が使えるサブスクリプション。</summary>
public sealed record SubscriptionInfo(string SubscriptionId, string DisplayName, string? State, string? TenantId);

/// <summary>サブスクリプションの一覧（MCP の list_subscriptions で、利用者がサブスクリプションを選べるようにする）。</summary>
public interface ISubscriptionDirectory
{
    Task<IReadOnlyList<SubscriptionInfo>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class AzureSubscriptionDirectory : ISubscriptionDirectory
{
    private readonly ArmClient _arm;

    public AzureSubscriptionDirectory(ArmClient arm)
    {
        _arm = arm;
    }

    public async Task<IReadOnlyList<SubscriptionInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SubscriptionInfo>();
        await foreach (var sub in _arm.GetSubscriptions().GetAllAsync(cancellationToken))
        {
            result.Add(new SubscriptionInfo(
                sub.Data.SubscriptionId, sub.Data.DisplayName, sub.Data.State?.ToString(), sub.Data.TenantId?.ToString()));
        }
        return result;
    }
}
