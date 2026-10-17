using Azure.Core;
using Azure.ResourceManager;

namespace Timetracker.Infrastructure.Azure;

/// <summary>サインインしたアカウントが入れるテナント。</summary>
public sealed record TenantInfo(string TenantId, string? DisplayName, string? DefaultDomain);

/// <summary>
/// テナントの一覧（デスクトップアプリで、テナントごとにサブスクリプションを読むため。トークンはテナントごとに発行される）。
/// </summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class AzureTenantDirectory : ITenantDirectory
{
    private readonly ArmClient _arm;

    public AzureTenantDirectory(ArmClient arm)
    {
        _arm = arm;
    }

    public async Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TenantInfo>();
        await foreach (var tenant in _arm.GetTenants().GetAllAsync(cancellationToken))
        {
            // テナント ID の無いものは、トークンを取れないため使えない
            if (tenant.Data.TenantId is { } id)
                result.Add(new TenantInfo(id.ToString(), tenant.Data.DisplayName, tenant.Data.DefaultDomain));
        }
        return result;
    }
}

/// <summary>
/// 指定したテナントのトークンを取る資格情報（ARM の呼び出しは要求にテナントを含まないため、資格情報の側でテナントを決める）。
/// claims（MFA などの追加の認証の要求）はそのまま渡す。
/// </summary>
public sealed class TenantScopedCredential : TokenCredential
{
    private readonly TokenCredential _inner;

    public TenantScopedCredential(TokenCredential inner, string tenantId)
    {
        _inner = inner;
        TenantId = tenantId;
    }

    public string TenantId { get; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        _inner.GetToken(ForTenant(requestContext), cancellationToken);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        _inner.GetTokenAsync(ForTenant(requestContext), cancellationToken);

    internal TokenRequestContext ForTenant(TokenRequestContext context) =>
        new(context.Scopes, parentRequestId: context.ParentRequestId, claims: context.Claims, tenantId: TenantId, isCaeEnabled: context.IsCaeEnabled);
}
