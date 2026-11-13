using System.Collections.ObjectModel;
using System.Globalization;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Desktop.Core.Pricing;

/// <summary>料金の目安の 1 行（TimeTracker・DB・Redis・永続化）。<see cref="Share"/> は合計に占める割合（0〜1。帯の幅）。</summary>
public sealed record CostLine(string Name, string Spec, string Amount, double Share);

/// <summary>単位の選択肢。</summary>
public sealed record CurrencyOption(PriceCurrency Currency, string Label);

/// <summary>
/// 新しくデプロイの料金の目安（オーナーの依頼、2026-11-13）。TimeTracker・DB・Redis のスペックと永続化の指定で、利用可能期限までの料金を、Container Apps の従量課金
/// （3 つとも 24 時間動き続けた場合の上限）と Azure Files（DB のデータ 1 GB の想定）で見積もる。期限なしは 30 日あたり。単位は切り替えられ、既定は円。
/// 単価は <see cref="IPriceCatalog"/>（Azure の公開の価格表。読めなければアプリに入れた価格）。
/// </summary>
public sealed class CostEstimateViewModel : ObservableObject
{
    /// <summary>Container Apps の無料枠（サブスクリプションごと、月ごと）。</summary>
    public const decimal FreeVcpuSeconds = 180_000m;

    public const decimal FreeGibSeconds = 360_000m;

    /// <summary>永続化した DB のデータの想定の量（GB）。</summary>
    public const decimal PersistedGb = 1m;

    public static readonly IReadOnlyList<CurrencyOption> CurrencyOptions = [new(PriceCurrency.Jpy, "円"), new(PriceCurrency.Usd, "USD")];

    private readonly SettingsViewModel _settings;
    private readonly TargetViewModel _target;
    private readonly IPriceCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly Dictionary<(string Region, PriceCurrency Currency), UnitPrices> _cache = [];
    private CurrencyOption _currency = CurrencyOptions[0];
    private UnitPrices? _prices;
    private string? _totalText;
    private string? _perDayText;
    private string? _periodText;
    private string? _savingText;
    private string _amountLabel = "期限まで";

    public CostEstimateViewModel(SettingsViewModel settings, TargetViewModel target, IPriceCatalog catalog, TimeProvider time)
    {
        _settings = settings;
        _target = target;
        _catalog = catalog;
        _time = time;
        _settings.InputChanged += (_, _) => Recalculate();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.Expiry) or nameof(SettingsViewModel.ExpiryDate) or nameof(SettingsViewModel.PersistDb))
                Recalculate();
        };
        _target.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TargetViewModel.Location))
                _ = RefreshAsync();
        };
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    /// <summary>単価を読み直す（「新しくデプロイ」を開いたとき・リージョンや単位を変えたとき）。</summary>
    public AsyncCommand RefreshCommand { get; }

    public CurrencyOption Currency
    {
        get => _currency;
        set
        {
            if (SetProperty(ref _currency, value))
                _ = RefreshAsync();
        }
    }

    public ObservableCollection<CostLine> Lines { get; } = [];

    /// <summary>期限までの合計（見積もれなければ null）。</summary>
    public string? TotalText
    {
        get => _totalText;
        private set => SetProperty(ref _totalText, value);
    }

    /// <summary>内訳の金額の期間（「期限まで」。期限なしは「1 か月」）。</summary>
    public string AmountLabel
    {
        get => _amountLabel;
        private set => SetProperty(ref _amountLabel, value);
    }

    /// <summary>1 日あたり。</summary>
    public string? PerDayText
    {
        get => _perDayText;
        private set => SetProperty(ref _perDayText, value);
    }

    /// <summary>見積もった期間（「期限（12/15 の終わり）までの 32 日」・期限を過ぎていれば、その旨）。</summary>
    public string? PeriodText
    {
        get => _periodText;
        private set => SetProperty(ref _periodText, value);
    }

    /// <summary>無料枠を使えた場合に安くなる額の上限。</summary>
    public string? SavingText
    {
        get => _savingText;
        private set => SetProperty(ref _savingText, value);
    }

    /// <summary>単価の出どころ（価格表を読んだ日時・アプリに入れた価格）。</summary>
    public string SourceText => _prices switch
    {
        { ReadAt: { } at } p => $"{p.Region} の従量課金（Azure の公開の価格表、{TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone):MM/dd HH:mm} に読んだ値）で、3 つとも 24 時間動き続けた場合の上限の目安です。実際の請求は使い方・割引・為替で変わります。",
        { } p => $"{p.Region} の従量課金（Azure の価格表を読めなかったため、アプリに入れた 2026-11-13 の東日本の価格）で、3 つとも 24 時間動き続けた場合の上限の目安です。実際の請求は使い方・割引・為替で変わります。",
        null => "料金の単価を読んでいます。",
    };

    public async Task RefreshAsync()
    {
        var key = (_target.Location, _currency.Currency);
        if (!_cache.TryGetValue(key, out var prices))
        {
            prices = await _catalog.GetAsync(key.Location, key.Currency);
            _cache[key] = prices;
        }
        // 読んでいる間に単位・リージョンを変えた場合は、新しいほうの読み込みに任せる
        if (key != (_target.Location, _currency.Currency))
            return;
        _prices = prices;
        OnPropertyChanged(nameof(SourceText));
        Recalculate();
    }

    private void Recalculate()
    {
        Lines.Clear();
        TotalText = PerDayText = SavingText = null;
        if (_prices is not { } p)
            return;
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var (end, label) = _settings.Expiry.Mode switch
        {
            ExpiryMode.Date => ((DateTimeOffset?)DeploymentExpiry.EndOfDay(DateOnly.FromDateTime(_settings.ExpiryDate.DateTime), zone), "期限"),
            ExpiryMode.None => (null, ""),
            _ => (DeploymentExpiry.DefaultExpiry(now, zone), "既定の期限"),
        };
        decimal seconds;
        if (end is { } until)
        {
            seconds = (decimal)(until - now).TotalSeconds;
            if (seconds <= 0)
            {
                PeriodText = "利用可能期限が過ぎているため、見積もれません（期限を直してください）。";
                return;
            }
            PeriodText = $"{label}（{TimeZoneInfo.ConvertTime(until, zone):MM/dd} の終わり）までの {Math.Ceiling(seconds / 86400m):0} 日";
        }
        else
        {
            // 期限なしは、目安として 1 か月（30 日）分と 1 日分を出す（オーナーの依頼、2026-11-13）
            seconds = 30m * 86400m;
            PeriodText = "期限なしのため、目安として 1 か月（30 日）分と 1 日分";
        }
        // 空の欄は既定値（デプロイと同じく DeployOptionsResolver が補う値）
        var apps = new[]
        {
            ("TimeTracker", Or(_settings.TtCpu, Defaults.TimetrackerCpu), Or(_settings.TtMemoryGi, Defaults.TimetrackerMemoryGi)),
            ("DB", Or(_settings.DbCpu, Defaults.DbCpu), Or(_settings.DbMemoryGi, Defaults.DbMemoryGi)),
            ("Redis", Or(_settings.RedisCpu, Defaults.RedisCpu), Or(_settings.RedisMemoryGi, Defaults.RedisMemoryGi)),
        };
        var parts = new List<(string Name, string Spec, decimal Amount)>();
        decimal vcpuSeconds = 0, gibSeconds = 0;
        foreach (var (name, cpuText, memoryText) in apps)
        {
            if (!TryParse(cpuText, out var cpu) || !TryParse(memoryText, out var memory))
            {
                PeriodText = $"{name} の CPU・メモリが数値でないため、見積もれません。";
                return;
            }
            vcpuSeconds += cpu * seconds;
            gibSeconds += memory * seconds;
            parts.Add((name, string.Create(CultureInfo.InvariantCulture, $"{cpu:0.##} vCPU / {memory:0.##} GiB"), seconds * (cpu * p.VcpuPerSecond + memory * p.GibPerSecond)));
        }
        if (_settings.PersistDb.Value == true)
            parts.Add(("永続化", $"Azure Files（DB のデータ {PersistedGb:0} GB の想定）", p.FilesPerGbMonth * PersistedGb * seconds / (30m * 86400m)));
        var total = parts.Sum(x => x.Amount);
        foreach (var (name, spec, amount) in parts)
            Lines.Add(new CostLine(name, spec, amount < 10 ? Money.Small(amount, p.Currency) : Money.Text(amount, p.Currency), (double)(amount / total)));
        AmountLabel = end == null ? "1 か月" : "期限まで";
        TotalText = end == null ? $"1 か月 約 {Money.Text(total, p.Currency)}" : $"約 {Money.Text(total, p.Currency)}";
        PerDayText = $"1 日 約 {Money.Text(total / (seconds / 86400m), p.Currency)}";
        var months = Math.Max(1m, Math.Ceiling(seconds / (30m * 86400m)));
        var saving = Math.Min(vcpuSeconds, FreeVcpuSeconds * months) * p.VcpuPerSecond + Math.Min(gibSeconds, FreeGibSeconds * months) * p.GibPerSecond;
        SavingText = $"Container Apps の無料枠（サブスクリプションごとに月 18 万 vCPU 秒・36 万 GiB 秒）がほかで使われていなければ、最大 約 {Money.Text(saving, p.Currency)} 安くなります。";
    }

    private static string Or(string text, double fallback) => string.IsNullOrWhiteSpace(text) ? fallback.ToString(CultureInfo.InvariantCulture) : text;

    private static bool TryParse(string text, out decimal value) => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
