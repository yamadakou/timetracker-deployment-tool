using System.Security.Cryptography;

namespace Timetracker.Application.Jobs;

/// <summary>
/// ツールの呼び出し元。計画とジョブには作成者を記録し、ほかの呼び出し元からは見えないようにする
/// （stdio では利用者は 1 人なので固定値。HTTP では Entra ID の oid を使う）。
/// </summary>
public interface ICallerContext
{
    string CallerId { get; }
}

/// <summary>stdio（ローカル）の呼び出し元。</summary>
public sealed class LocalCallerContext : ICallerContext
{
    public const string Id = "local";

    public string CallerId => Id;
}

public enum PlanLookupStatus
{
    Found,
    NotFound,
    Expired,
}

/// <summary>計画を取り出した結果。<see cref="Plan"/> は <see cref="PlanLookupStatus.Found"/> のときだけ値がある。</summary>
public sealed record PlanLookup<TPlan>(PlanLookupStatus Status, TPlan? Plan) where TPlan : class;

/// <summary>保存した計画の ID と有効期限。</summary>
public sealed record SavedPlan(string PlanId, DateTimeOffset ExpiresAt);

/// <summary>計画（plan_* の結果）の保存先。計画は一度だけ取り出せる（同じ計画で 2 回実行できないように）。</summary>
public interface IPlanStore
{
    SavedPlan Save<TPlan>(TPlan plan, string callerId) where TPlan : class;

    /// <summary>計画を読む（無効にしない）。実行の前に条件（同意など）を確かめるために使う。</summary>
    PlanLookup<TPlan> Find<TPlan>(string planId, string callerId) where TPlan : class;

    /// <summary>計画を取り出して無効にする。ほかの呼び出し元の計画や種類の違う計画は、存在しないものとして扱う。</summary>
    PlanLookup<TPlan> Take<TPlan>(string planId, string callerId) where TPlan : class;
}

/// <summary>
/// プロセス内のメモリに置く計画の保存先。計画にはパスワードを含む設定が入るため、永続化しない。
/// </summary>
public sealed class InMemoryPlanStore : IPlanStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);

    private sealed record Entry(object Plan, string CallerId, DateTimeOffset ExpiresAt);

    // 取り出しと削除を同時に行い、同じ計画が並行して 2 回取り出されないよう、ロックの中で操作する
    private readonly Dictionary<string, Entry> _plans = new();
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;

    public InMemoryPlanStore(TimeProvider time, TimeSpan? lifetime = null)
    {
        _time = time;
        _lifetime = lifetime ?? DefaultLifetime;
    }

    public SavedPlan Save<TPlan>(TPlan plan, string callerId) where TPlan : class
    {
        var now = _time.GetUtcNow();
        var saved = new SavedPlan(NewId("pln_"), now + _lifetime);
        lock (_lock)
        {
            // 期限切れの計画を捨てる（取り出されなかった計画がたまらないように）
            foreach (var expired in _plans.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToList())
                _plans.Remove(expired);
            _plans[saved.PlanId] = new Entry(plan, callerId, saved.ExpiresAt);
        }
        return saved;
    }

    public PlanLookup<TPlan> Find<TPlan>(string planId, string callerId) where TPlan : class => Lookup<TPlan>(planId, callerId, remove: false);

    public PlanLookup<TPlan> Take<TPlan>(string planId, string callerId) where TPlan : class => Lookup<TPlan>(planId, callerId, remove: true);

    private PlanLookup<TPlan> Lookup<TPlan>(string planId, string callerId, bool remove) where TPlan : class
    {
        Entry? entry;
        lock (_lock)
        {
            // ほかの呼び出し元のもの・種類の違うものは取り出さない（存在しないものとして扱う）
            if (!_plans.TryGetValue(planId, out entry) || entry.CallerId != callerId || entry.Plan is not TPlan)
                return new PlanLookup<TPlan>(PlanLookupStatus.NotFound, null);
            if (remove)
                _plans.Remove(planId);
        }
        return entry.ExpiresAt <= _time.GetUtcNow()
            ? new PlanLookup<TPlan>(PlanLookupStatus.Expired, null)
            : new PlanLookup<TPlan>(PlanLookupStatus.Found, (TPlan)entry.Plan);
    }

    /// <summary>推測されにくい ID（接頭辞 + 16 バイトの乱数の 16 進数）。</summary>
    internal static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
