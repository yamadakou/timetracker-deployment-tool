using System.Globalization;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.Pricing;

/// <summary>
/// デプロイの一覧の 1 行の料金の目安（<see cref="Total"/> はデプロイから期限まで。期限なし・記録なしは 1 か月〔30 日〕分で、<see cref="IsMonthly"/>）。
/// <see cref="ThisMonth"/> は今月の 1 日（デプロイがその後なら、デプロイの日時）から月末（期限が先なら、期限）までの見込み。
/// </summary>
public sealed record DeploymentCost(decimal Total, decimal PerDay, decimal ThisMonth, bool IsMonthly, PriceCurrency Currency)
{
    /// <summary>一覧の列の文（「約 1,230 円」・期限なしは「1 か月 約 …」）。</summary>
    public string Text => (IsMonthly ? "1 か月 約 " : "約 ") + Money.Text(Total, Currency);

    /// <summary>一覧の列の 2 行目（1 日あたり）。</summary>
    public string PerDayText => "1 日 約 " + Money.Text(PerDay, Currency);
}

/// <summary>
/// デプロイの一覧の料金の目安（オーナーの依頼、2026-11-14: 列に各デプロイの目安、上部に全体と今月分の合計。オーナーの判断: スペックからの目安、
/// 列は期限まで・今月分は月末まで）。「新しくデプロイ」の料金の目安（<see cref="CostEstimateViewModel"/>）と同じく、Container Apps の従量課金で
/// 3 つとも 24 時間動き続けた場合の上限と、永続化した DB のデータ 1 GB の Azure Files で見積もる（無料枠は引かない）。CPU・メモリの読めないアプリは既定のスペック、
/// 無いアプリは数えない。期限を過ぎても残っているものは、今まで動いた分として、今の時刻までを数える。単価はリージョンごとに読み、覚えておく。
/// </summary>
public sealed class DeploymentCostBook : ObservableObject
{
    private const int SecondsPerDay = 86400;

    private readonly IPriceCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly Dictionary<(string Region, PriceCurrency Currency), UnitPrices> _prices = [];
    private CurrencyOption _currency = CostEstimateViewModel.CurrencyOptions[0];

    public DeploymentCostBook(IPriceCatalog catalog, TimeProvider time)
    {
        _catalog = catalog;
        _time = time;
    }

    /// <summary>単位を変えたとき（読み直す）。</summary>
    public event Action CurrencyChanged = () => { };

    /// <summary>単位（既定は円）。</summary>
    public CurrencyOption Currency
    {
        get => _currency;
        set
        {
            if (SetProperty(ref _currency, value))
                CurrencyChanged.Invoke();
        }
    }

    /// <summary>リージョンの無い行（反映前のデプロイ）に使うリージョン。</summary>
    public const string DefaultRegion = "japaneast";

    /// <summary>行のリージョンの単価を、まだ読んでいなければ読む。新しく読んだものがあれば true。</summary>
    public async Task<bool> LoadAsync(IEnumerable<DeploymentEntry> entries)
    {
        var currency = _currency.Currency;
        var loaded = false;
        foreach (var region in entries.Select(Region).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            if (_prices.ContainsKey((region, currency)))
                continue;
            _prices[(region, currency)] = await _catalog.GetAsync(region, currency);
            loaded = true;
        }
        return loaded;
    }

    /// <summary>行のリージョンの単価を、今の単位で読み終えたか。</summary>
    public bool HasPricesFor(IEnumerable<DeploymentEntry> entries) => entries.All(e => _prices.ContainsKey((Region(e), _currency.Currency)));

    /// <summary>行の料金の目安（単価をまだ読んでいない・リソースグループだけの行・数えるものが無い行は null）。</summary>
    public DeploymentCost? For(DeploymentEntry entry)
    {
        if (PerSecond(entry) is not var (perSecond, currency))
            return null;
        var now = _time.GetUtcNow();
        var start = entry.DeployedAt ?? now;
        // 期限を過ぎても残っていれば、今の時刻まで動いたとして数える
        DateTimeOffset? end = entry is { ExpiryKind: ExpiryKind.Date, ExpiresAt: { } at } ? at > now ? at : now : null;
        var total = end is { } until ? perSecond * Seconds(start, until) : perSecond * 30m * SecondsPerDay;
        var (monthStart, monthEnd) = ThisMonthRange();
        var from = start > monthStart ? start : monthStart;
        var to = end is { } e && e < monthEnd ? e : monthEnd;
        return new DeploymentCost(total, perSecond * SecondsPerDay, perSecond * Seconds(from, to), end == null, currency);
    }

    /// <summary>
    /// このアプリで削除したデプロイの、デプロイから削除までの料金の目安（<see cref="DeploymentCost.ThisMonth"/> は今月に動いた分）。
    /// デプロイの日時が分からない・単価をまだ読んでいない・数えるものが無いものは null。
    /// </summary>
    public DeploymentCost? ForDeleted(DeletedDeployment deleted)
    {
        if (deleted.Entry.DeployedAt is not { } start || PerSecond(deleted.Entry) is not var (perSecond, currency))
            return null;
        // 削除したのは今より前のため、今月の終わりは削除した時刻
        var (monthStart, _) = ThisMonthRange();
        var from = start > monthStart ? start : monthStart;
        return new DeploymentCost(perSecond * Seconds(start, deleted.DeletedAt), perSecond * SecondsPerDay, perSecond * Seconds(from, deleted.DeletedAt), false, currency);
    }

    /// <summary>単価を読む・読み終えたかを確かめる対象（削除したデプロイのリージョンも含める）。</summary>
    public static IEnumerable<DeploymentEntry> PricedEntries(IEnumerable<DeploymentEntry> entries, IEnumerable<DeletedDeployment> deleted) =>
        entries.Concat(deleted.Select(d => d.Entry));

    // 1 秒あたりの料金（単価をまだ読んでいない・リソースグループだけ・数えるものが無ければ null）
    private (decimal PerSecond, PriceCurrency Currency)? PerSecond(DeploymentEntry entry)
    {
        if (entry.IsResourceGroupOnly || !_prices.TryGetValue((Region(entry), _currency.Currency), out var p))
            return null;
        var apps = new[]
        {
            (entry.Timetracker, Defaults.TimetrackerCpu, Defaults.TimetrackerMemoryGi),
            (entry.Db, Defaults.DbCpu, Defaults.DbMemoryGi),
            (entry.Redis, Defaults.RedisCpu, Defaults.RedisMemoryGi),
        };
        var perSecond = apps.Where(a => a.Item1 != null)
            .Sum(a => (decimal)(a.Item1!.Cpu ?? a.Item2) * p.VcpuPerSecond + Gib(a.Item1.Memory, a.Item3) * p.GibPerSecond);
        if (entry.DbPersisted)
            perSecond += p.FilesPerGbMonth * CostEstimateViewModel.PersistedGb / (30m * SecondsPerDay);
        return perSecond == 0 ? null : (perSecond, p.Currency);
    }

    // 今月の 1 日から月末まで（PC のタイムゾーン）
    private (DateTimeOffset Start, DateTimeOffset End) ThisMonthRange()
    {
        var zone = _time.LocalTimeZone;
        var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone);
        return (Local(new DateTime(today.Year, today.Month, 1), zone), Local(new DateTime(today.Year, today.Month, 1).AddMonths(1), zone));
    }

    /// <summary>上部の「全体」（読んだすべての行の列の合計。期限なしの行は 1 か月分）。数えられる行が無ければ null。</summary>
    public string? TotalText(IReadOnlyList<DeploymentEntry> entries)
    {
        var costs = entries.Select(For).OfType<DeploymentCost>().ToList();
        if (costs.Count == 0)
            return null;
        return "約 " + Money.Text(costs.Sum(c => c.Total), _currency.Currency);
    }

    /// <summary>「全体」の説明（期限なしの行の数え方）。</summary>
    public string? TotalNote(IReadOnlyList<DeploymentEntry> entries)
    {
        var costs = entries.Select(For).OfType<DeploymentCost>().ToList();
        if (costs.Count == 0)
            return null;
        var monthly = costs.Count(c => c.IsMonthly);
        return monthly == 0 ? $"今ある {costs.Count} 件のデプロイの、デプロイから期限まで" : $"今ある {costs.Count} 件のデプロイの、デプロイから期限まで（期限なしの {monthly} 件は 1 か月分）";
    }

    /// <summary>
    /// 上部の「今月分」（今あるデプロイの今月の 1 日から月末までの見込みと、今月に削除したデプロイの削除までの分の合計）。数えられるものが無ければ null。
    /// </summary>
    public string? ThisMonthText(IReadOnlyList<DeploymentEntry> entries, IReadOnlyList<DeletedDeployment> deleted)
    {
        var costs = entries.Select(For).Concat(DeletedThisMonth(deleted)).OfType<DeploymentCost>().ToList();
        return costs.Count == 0 ? null : "約 " + Money.Text(costs.Sum(c => c.ThisMonth), _currency.Currency);
    }

    /// <summary>上部の「削除したデプロイ」（今月に削除したデプロイの、今月の 1 日〔デプロイがその後ならデプロイ〕から削除までの分）。数えられるものが無ければ null。</summary>
    public string? DeletedText(IReadOnlyList<DeletedDeployment> deleted)
    {
        var costs = DeletedThisMonth(deleted).OfType<DeploymentCost>().ToList();
        return costs.Count == 0 ? null : "約 " + Money.Text(costs.Sum(c => c.ThisMonth), _currency.Currency);
    }

    /// <summary>「削除したデプロイ」の説明。数えられるものが無ければ null。</summary>
    public string? DeletedNote(IReadOnlyList<DeletedDeployment> deleted)
    {
        var count = DeletedThisMonth(deleted).OfType<DeploymentCost>().Count();
        return count == 0 ? null : $"今月このアプリで削除した {count} 件の、削除した日時までの分（今月分に含みます）";
    }

    // 今月に削除したもの（先月までに削除したものは、今月の料金に入らない）
    private IEnumerable<DeploymentCost?> DeletedThisMonth(IReadOnlyList<DeletedDeployment> deleted)
    {
        var (monthStart, _) = ThisMonthRange();
        return deleted.Where(d => d.DeletedAt > monthStart).Select(ForDeleted);
    }

    /// <summary>「今月分」の見出し（「11 月の見込み」）。</summary>
    public string ThisMonthLabel => $"{TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).Month} 月の見込み";

    /// <summary>目安の前提（一覧の上に出す）。</summary>
    public const string Note = "料金は目安です。スペックと Azure の公開の価格表（読めなければアプリに入れた価格）から、3 つとも 24 時間動き続けた場合の上限で見積もります" +
                               "（無料枠・割引は引きません。実際の請求は使い方・為替で変わります）。";

    /// <summary>
    /// 数える範囲（オーナーの指摘、2026-11-16: 削除すると、削除した日までの料金が無かったことになると勘違いしやすい）。
    /// </summary>
    public const string ScopeNote = "「全体」は今あるデプロイだけを数えます。削除したデプロイも、削除した日時までの料金はかかっています。" +
                                    "このアプリで削除したものは「削除したデプロイ」と「今月分」に含めますが、CLI・MCP サーバー・別の PC で削除したものは含みません。" +
                                    "実際の請求は、Azure Portal の Cost Management で確かめてください。";

    private static string Region(DeploymentEntry entry) => entry.Location is { Length: > 0 } location ? location : DefaultRegion;

    private static decimal Seconds(DateTimeOffset from, DateTimeOffset to) => to > from ? (decimal)(to - from).TotalSeconds : 0m;

    private static DateTimeOffset Local(DateTime local, TimeZoneInfo zone) => new(local, zone.GetUtcOffset(local));

    // Container Apps のメモリの表記（「1.5Gi」）。読めなければ既定値
    private static decimal Gib(string? memory, double fallback) =>
        decimal.TryParse(memory?.Replace("Gi", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (decimal)fallback;
}
