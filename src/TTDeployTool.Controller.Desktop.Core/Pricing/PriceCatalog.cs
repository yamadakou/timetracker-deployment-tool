using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TTDeployTool.Controller.Desktop.Core.Pricing;

/// <summary>料金の単位（オーナーの依頼、2026-11-13: 単位は切り替えられ、既定は円）。</summary>
public enum PriceCurrency
{
    Jpy,
    Usd,
}

/// <summary>
/// 料金の目安に使う単価（Container Apps の従量課金〔Consumption〕の vCPU 秒・GiB 秒、Azure Files〔ホット・LRS〕の GB 月）。
/// <see cref="ReadAt"/> は Azure の価格表を読んだ日時（アプリに入れた価格なら null）。
/// </summary>
public sealed record UnitPrices(PriceCurrency Currency, string Region, decimal VcpuPerSecond, decimal GibPerSecond, decimal FilesPerGbMonth, DateTimeOffset? ReadAt);

/// <summary>料金の単価を読む（オーナーの判断、2026-11-13 の案 1: Azure の公開の価格表をその場で読み、読めなければアプリに入れた価格）。</summary>
public interface IPriceCatalog
{
    Task<UnitPrices> GetAsync(string region, PriceCurrency currency, CancellationToken cancellationToken = default);
}

/// <summary>アプリに入れた価格（2026-11-13 に、東日本〔japaneast〕の公開の価格表で読んだ値）。通信できないときと、テストで使う。</summary>
public sealed class BuiltInPriceCatalog : IPriceCatalog
{
    /// <summary>円とドルの比（2026-11-13 の価格表の値から求めた値）。</summary>
    public const decimal JpyPerUsd = 158.2m;

    public static readonly UnitPrices Usd = new(PriceCurrency.Usd, "japaneast", 0.000024m, 0.000003m, 0.030m, null);

    public static readonly UnitPrices Jpy = new(PriceCurrency.Jpy, "japaneast", 0.000024m * JpyPerUsd, 0.000003m * JpyPerUsd, 4.7469m, null);

    public Task<UnitPrices> GetAsync(string region, PriceCurrency currency, CancellationToken cancellationToken = default) =>
        Task.FromResult((currency == PriceCurrency.Jpy ? Jpy : Usd) with { Region = region });
}

/// <summary>
/// Azure の公開の価格表（Retail Prices API。https://prices.azure.com/api/retail/prices 。サインインは要らない）から読む。円の価格は 4 桁目で丸められているため
/// （メモリの単価で 5% ほどずれる）、Container Apps は USD の価格を読み、同じ価格表の最も高い項目（同じ meterId）の比で換算する。Azure Files は、その単位の価格を使う。
/// 読めなければ <see cref="BuiltInPriceCatalog"/> の価格（例外は投げない）。
/// </summary>
public sealed class AzureRetailPriceCatalog : IPriceCatalog
{
    public const string Endpoint = "https://prices.azure.com/api/retail/prices";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly IPriceCatalog _fallback;

    public AzureRetailPriceCatalog(HttpClient http, TimeProvider time, IPriceCatalog? fallback = null)
    {
        _http = http;
        _time = time;
        _fallback = fallback ?? new BuiltInPriceCatalog();
    }

    public async Task<UnitPrices> GetAsync(string region, PriceCurrency currency, CancellationToken cancellationToken = default)
    {
        try
        {
            var apps = $"serviceName eq 'Azure Container Apps' and armRegionName eq '{region}' and priceType eq 'Consumption'";
            var usd = await ReadAsync("USD", apps, cancellationToken);
            var vcpu = Price(usd, "Standard vCPU Active Usage");
            var gib = Price(usd, "Standard Memory Active Usage");
            var files = $"serviceName eq 'Storage' and armRegionName eq '{region}' and productName eq 'Files v2' and skuName eq 'Hot LRS' and priceType eq 'Consumption'";
            var code = currency == PriceCurrency.Jpy ? "JPY" : "USD";
            var filesPrice = Price(await ReadAsync(code, files, cancellationToken), "Hot LRS Data Stored");
            var rate = 1m;
            if (currency == PriceCurrency.Jpy)
            {
                // 同じ meterId の最も高い項目で、円とドルの比を求める
                var jpy = await ReadAsync("JPY", apps, cancellationToken);
                var pairs = usd.Where(u => u.RetailPrice > 0)
                    .Join(jpy, u => u.MeterId, j => j.MeterId, (u, j) => (Usd: u.RetailPrice, Jpy: j.RetailPrice))
                    .ToList();
                if (pairs.Count == 0)
                    throw new InvalidDataException("円とドルの比を求められませんでした。");
                var pair = pairs.MaxBy(p => p.Usd);
                rate = pair.Jpy / pair.Usd;
            }
            return new UnitPrices(currency, region, vcpu * rate, gib * rate, filesPrice, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
        {
            return await _fallback.GetAsync(region, currency, cancellationToken);
        }
    }

    private static decimal Price(IReadOnlyList<RetailPriceItem> items, string meter) =>
        items.FirstOrDefault(i => i.MeterName == meter && i.TierMinimumUnits == 0)?.RetailPrice
        ?? throw new InvalidDataException($"価格表に {meter} がありません。");

    // 価格表の続き（NextPageLink）も読む
    private async Task<IReadOnlyList<RetailPriceItem>> ReadAsync(string currency, string filter, CancellationToken cancellationToken)
    {
        var items = new List<RetailPriceItem>();
        string? url = $"{Endpoint}?currencyCode={currency}&$filter={Uri.EscapeDataString(filter)}";
        while (url != null)
        {
            var page = await _http.GetFromJsonAsync<RetailPricePage>(url, cancellationToken) ?? throw new InvalidDataException("価格表が空です。");
            items.AddRange(page.Items ?? []);
            url = page.NextPageLink;
        }
        return items;
    }

    internal sealed record RetailPricePage(
        [property: JsonPropertyName("Items")] IReadOnlyList<RetailPriceItem>? Items,
        [property: JsonPropertyName("NextPageLink")] string? NextPageLink);

    internal sealed record RetailPriceItem(
        [property: JsonPropertyName("meterId")] string MeterId,
        [property: JsonPropertyName("meterName")] string MeterName,
        [property: JsonPropertyName("retailPrice")] decimal RetailPrice,
        [property: JsonPropertyName("tierMinimumUnits")] decimal TierMinimumUnits);
}

/// <summary>金額の表示（円は 10 円単位、1 万円以上は 100 円単位。USD は 2 桁の小数）。</summary>
public static class Money
{
    public static string Text(decimal amount, PriceCurrency currency) => currency == PriceCurrency.Jpy
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(amount / (amount >= 10000 ? 100 : 10), MidpointRounding.AwayFromZero) * (amount >= 10000 ? 100 : 10):#,0} 円")
        : string.Create(CultureInfo.InvariantCulture, $"${amount:#,0.00}");

    /// <summary>1 円・1 セントより細かく（1 日の永続化など、小さな金額）。</summary>
    public static string Small(decimal amount, PriceCurrency currency) => currency == PriceCurrency.Jpy
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(amount, MidpointRounding.AwayFromZero)):#,0} 円")
        : string.Create(CultureInfo.InvariantCulture, $"${amount:#,0.00}");
}
