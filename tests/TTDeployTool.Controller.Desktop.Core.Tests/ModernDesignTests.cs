using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Pricing;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>価格表を返す HttpMessageHandler（URL ごとの応答。無ければ 404）。</summary>
internal sealed class FakePrices : HttpMessageHandler
{
    public Dictionary<string, string> Pages { get; } = [];

    public List<string> Requests { get; } = [];

    public Exception? Error { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        if (Error != null)
            throw Error;
        // 価格表は東日本（japaneast）のものだけ
        var key = url.Contains("japaneast", StringComparison.Ordinal) || url.Contains("page2", StringComparison.Ordinal) ? Pages.Keys.FirstOrDefault(url.Contains) : null;
        return Task.FromResult(key == null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Pages[key], Encoding.UTF8, "application/json") });
    }

    public static string Page(string? next, params (string Meter, string Id, decimal Price, decimal Tier)[] items) =>
        "{\"Items\":[" + string.Join(",", items.Select(i =>
            $"{{\"meterId\":\"{i.Id}\",\"meterName\":\"{i.Meter}\",\"retailPrice\":{i.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"tierMinimumUnits\":{i.Tier}}}"))
        + "],\"NextPageLink\":" + (next == null ? "null" : $"\"{next}\"") + "}";

    /// <summary>東日本の価格表（2026-11-13 の値）。USD の Container Apps は 2 ページに分ける。</summary>
    public static FakePrices JapanEast()
    {
        var fake = new FakePrices();
        fake.Pages["currencyCode=USD&$filter=serviceName%20eq%20%27Azure%20Container%20Apps"] = Page("https://prices.azure.com/api/retail/prices?page2",
            ("Standard vCPU Active Usage", "cpu", 0.000024m, 0), ("Standard vCPU Active Usage", "cpu", 0.00001m, 100), ("Dedicated GPU Usage", "gpu", 6.392m, 0));
        fake.Pages["page2"] = Page(null, ("Standard Memory Active Usage", "mem", 0.000003m, 0), ("Free", "free", 0m, 0));
        fake.Pages["currencyCode=JPY&$filter=serviceName%20eq%20%27Azure%20Container%20Apps"] = Page(null,
            ("Standard vCPU Active Usage", "cpu", 0.0038m, 0), ("Dedicated GPU Usage", "gpu", 1011.2511m, 0));
        fake.Pages["currencyCode=JPY&$filter=serviceName%20eq%20%27Storage"] = Page(null, ("Hot LRS Data Stored", "files", 4.7469m, 0));
        fake.Pages["currencyCode=USD&$filter=serviceName%20eq%20%27Storage"] = Page(null, ("Hot LRS Data Stored", "files", 0.03m, 0));
        return fake;
    }
}

/// <summary>
/// 推奨のデザイン（オーナーの判断、2026-11-13）の Core: 料金の目安（Azure の公開の価格表・円と USD）、スペックのスライダー、件数のカードと絞り込み、
/// 期限の残りと「+7 日」「+30 日」。
/// </summary>
public sealed class ModernDesignTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Tokyo()
    {
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TokyoTime.Zone);
        return time;
    }

    // ---------------- 単価

    [Fact]
    public async Task Prices_Should_Be_Read_From_The_Retail_Prices_And_Converted_With_The_Largest_Meter()
    {
        var fake = FakePrices.JapanEast();
        var time = Tokyo();
        var catalog = new AzureRetailPriceCatalog(new HttpClient(fake), time);

        var jpy = await catalog.GetAsync("japaneast", PriceCurrency.Jpy);
        var rate = 1011.2511m / 6.392m;
        jpy.Should().Be(new UnitPrices(PriceCurrency.Jpy, "japaneast", 0.000024m * rate, 0.000003m * rate, 4.7469m, Now));
        var usd = await catalog.GetAsync("japaneast", PriceCurrency.Usd);
        (usd.VcpuPerSecond, usd.GibPerSecond, usd.FilesPerGbMonth).Should().Be((0.000024m, 0.000003m, 0.03m));
        fake.Requests.Should().Contain(r => r.Contains("page2"), "続きのページも読む");
        fake.Requests.Should().OnlyContain(r => r.StartsWith(AzureRetailPriceCatalog.Endpoint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prices_Should_Fall_Back_To_The_Built_In_Ones()
    {
        var time = Tokyo();
        var down = new FakePrices { Error = new HttpRequestException("つながらない") };
        (await new AzureRetailPriceCatalog(new HttpClient(down), time).GetAsync("westus", PriceCurrency.Jpy))
            .Should().Be(BuiltInPriceCatalog.Jpy with { Region = "westus" });

        // 項目が無い・円とドルの比が求められない・空の応答
        var missing = FakePrices.JapanEast();
        missing.Pages["page2"] = FakePrices.Page(null);
        (await new AzureRetailPriceCatalog(new HttpClient(missing), time).GetAsync("japaneast", PriceCurrency.Usd)).ReadAt.Should().BeNull();
        var noRate = FakePrices.JapanEast();
        noRate.Pages["currencyCode=JPY&$filter=serviceName%20eq%20%27Azure%20Container%20Apps"] = FakePrices.Page(null);
        (await new AzureRetailPriceCatalog(new HttpClient(noRate), time).GetAsync("japaneast", PriceCurrency.Jpy)).ReadAt.Should().BeNull();
        var empty = FakePrices.JapanEast();
        empty.Pages["page2"] = "null";
        (await new AzureRetailPriceCatalog(new HttpClient(empty), time).GetAsync("japaneast", PriceCurrency.Usd)).Should().Be(BuiltInPriceCatalog.Usd);
        var broken = FakePrices.JapanEast();
        broken.Pages["page2"] = "{";
        (await new AzureRetailPriceCatalog(new HttpClient(broken), time, new BuiltInPriceCatalog()).GetAsync("japaneast", PriceCurrency.Usd)).ReadAt.Should().BeNull();
        var noItems = FakePrices.JapanEast();
        noItems.Pages["page2"] = "{\"Items\":null,\"NextPageLink\":null}";
        (await new AzureRetailPriceCatalog(new HttpClient(noItems), time).GetAsync("japaneast", PriceCurrency.Usd)).ReadAt.Should().BeNull();
    }

    [Theory]
    [InlineData(4.4, "0 円", "4 円")]
    [InlineData(0.2, "0 円", "1 円")]
    [InlineData(9994, "9,990 円", "9,994 円")]
    [InlineData(10049, "10,000 円", "10,049 円")]
    [InlineData(29020.4, "29,000 円", "29,020 円")]
    public void Yen_Should_Be_Rounded_To_Tens_Or_Hundreds(double amount, string text, string small)
    {
        (Money.Text((decimal)amount, PriceCurrency.Jpy), Money.Small((decimal)amount, PriceCurrency.Jpy)).Should().Be((text, small));
        Money.Text(1234.567m, PriceCurrency.Usd).Should().Be("$1,234.57");
        Money.Small(0.004m, PriceCurrency.Usd).Should().Be("$0.00");
    }

    // ---------------- 料金の目安

    private static async Task<(Harness H, FakeTimeProvider Time)> WizardAsync(IPriceCatalog? prices = null)
    {
        var time = Tokyo();
        var h = new Harness(time: time, prices: prices);
        await h.ReadyAsync();
        await h.Main.PageShownAsync(Pages.Wizard);
        return (h, time);
    }

    [Fact]
    public async Task The_Cost_Should_Be_Estimated_Until_The_Default_Expiry_In_Yen()
    {
        var (h, _) = await WizardAsync();
        var cost = h.Main.Cost;

        // 既定の期限は今日（11/13）から 32 日後（12/15）の終わり。3 つとも 24 時間動き続けた場合
        cost.Currency.Should().Be(CostEstimateViewModel.CurrencyOptions[0], "既定は円");
        cost.PeriodText.Should().Be("既定の期限（12/15 の終わり）までの 33 日");
        var seconds = (decimal)(DeploymentExpiry.EndOfDay(new DateOnly(2026, 12, 15), TokyoTime.Zone) - Now).TotalSeconds;
        var p = BuiltInPriceCatalog.Jpy;
        decimal App(decimal cpu, decimal memory) => seconds * (cpu * p.VcpuPerSecond + memory * p.GibPerSecond);
        var tt = App(1.5m, 3.0m);
        var total = tt + App(1.0m, 2.0m) + App(0.5m, 1.0m);
        cost.Lines.Select(l => (l.Name, l.Spec, l.Amount)).Should().Equal(
            ("TimeTracker", "1.5 vCPU / 3 GiB", Money.Text(tt, PriceCurrency.Jpy)), ("DB", "1 vCPU / 2 GiB", Money.Text(App(1.0m, 2.0m), PriceCurrency.Jpy)),
            ("Redis", "0.5 vCPU / 1 GiB", Money.Text(App(0.5m, 1.0m), PriceCurrency.Jpy)));
        cost.Lines.Sum(l => l.Share).Should().BeApproximately(1, 1e-9);
        cost.TotalText.Should().Be($"約 {Money.Text(total, PriceCurrency.Jpy)}");
        cost.PerDayText.Should().Be($"1 日 約 {Money.Text(total / (seconds / 86400m), PriceCurrency.Jpy)}");
        cost.SavingText.Should().StartWith("Container Apps の無料枠（サブスクリプションごとに月 18 万 vCPU 秒・36 万 GiB 秒）がほかで使われていなければ、最大 約 ");
        cost.SourceText.Should().Contain("アプリに入れた 2026-11-13 の東日本の価格");
    }

    [Fact]
    public async Task The_Cost_Should_Follow_The_Inputs_And_The_Currency()
    {
        var fake = FakePrices.JapanEast();
        var (h, time) = await WizardAsync(new AzureRetailPriceCatalog(new HttpClient(fake), Tokyo()));
        var cost = h.Main.Cost;
        var settings = h.Main.Settings;
        cost.SourceText.Should().Contain("Azure の公開の価格表、11/13 09:00 に読んだ値");

        // 永続化すると Azure Files の行が増える（1 GB の想定。小さな金額は 1 円単位）
        settings.PersistDb = SettingsViewModel.PersistDbOptions[1];
        cost.Lines.Select(l => l.Name).Should().Equal("TimeTracker", "DB", "Redis", "永続化");
        cost.Lines[3].Spec.Should().Be("Azure Files（DB のデータ 1 GB の想定）");
        cost.Lines[3].Amount.Should().Be("5 円");

        // 期限を日付で指定する・期限なしは 30 日あたり・過ぎた日付は見積もらない
        settings.Expiry = SettingsViewModel.ExpiryOptions[1];
        settings.ExpiryDate = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.FromHours(9));
        (cost.PeriodText, cost.AmountLabel).Should().Be(("期限（11/20 の終わり）までの 8 日", "期限まで"));
        cost.TotalText.Should().StartWith("約 ");
        settings.Expiry = SettingsViewModel.ExpiryOptions[2];
        cost.PeriodText.Should().Be("期限なしのため、目安として 1 か月（30 日）分と 1 日分");
        (cost.AmountLabel, cost.TotalText, cost.PerDayText).Should().Match<(string, string?, string?)>(x =>
            x.Item1 == "1 か月" && x.Item2!.StartsWith("1 か月 約 ") && x.Item3!.StartsWith("1 日 約 "));
        settings.Expiry = SettingsViewModel.ExpiryOptions[1];
        settings.ExpiryDate = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(9));
        (cost.PeriodText, cost.TotalText, cost.Lines.Count).Should().Be(("利用可能期限が過ぎているため、見積もれません（期限を直してください）。", (string?)null, 0));

        // 読めない数値・空の欄（既定値）
        settings.Expiry = SettingsViewModel.ExpiryOptions[0];
        settings.DbCpu = "x";
        cost.PeriodText.Should().Be("DB の CPU・メモリが数値でないため、見積もれません。");
        settings.DbCpu = "";
        cost.Lines[1].Spec.Should().Be("1 vCPU / 2 GiB");

        // 単位を USD にすると、USD の価格を読み直す。同じリージョン・単位は読み直さない
        var before = fake.Requests.Count;
        cost.Currency = CostEstimateViewModel.CurrencyOptions[0];
        fake.Requests.Count.Should().Be(before, "同じ単位なら読み直さない");
        cost.Currency = CostEstimateViewModel.CurrencyOptions[1];
        await WaitAsync(() => cost.TotalText?.StartsWith("約 $", StringComparison.Ordinal) == true);
        fake.Requests.Count.Should().BeGreaterThan(before);
        before = fake.Requests.Count;
        await cost.RefreshCommand.ExecuteAsync();
        fake.Requests.Count.Should().Be(before, "読んだ単価は覚えておく");

        // リージョンを変えると読み直す（価格表に無ければアプリに入れた価格）
        h.Main.Target.Location = "westus";
        await WaitAsync(() => cost.SourceText.Contains("westus"));
        cost.SourceText.Should().Contain("アプリに入れた");
    }

    [Fact]
    public async Task A_Late_Price_For_An_Old_Currency_Should_Not_Replace_The_New_One()
    {
        var gate = new TaskCompletionSource<UnitPrices>();
        var catalog = new Mock<IPriceCatalog>();
        catalog.Setup(c => c.GetAsync(It.IsAny<string>(), PriceCurrency.Jpy, It.IsAny<CancellationToken>())).Returns(gate.Task);
        catalog.Setup(c => c.GetAsync(It.IsAny<string>(), PriceCurrency.Usd, It.IsAny<CancellationToken>())).ReturnsAsync(BuiltInPriceCatalog.Usd);
        var time = Tokyo();
        var h = await new Harness(time: time, prices: catalog.Object).ReadyAsync();
        var cost = h.Main.Cost;
        cost.SourceText.Should().Be("料金の単価を読んでいます。");
        cost.TotalText.Should().BeNull("単価を読むまでは見積もらない");
        var yen = cost.RefreshAsync();
        cost.Currency = CostEstimateViewModel.CurrencyOptions[1];
        await WaitAsync(() => cost.TotalText != null);
        gate.SetResult(BuiltInPriceCatalog.Jpy);
        await yen;
        cost.TotalText.Should().StartWith("約 $", "後から届いた円の単価で上書きしない");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        condition().Should().BeTrue();
    }

    // ---------------- スペックのスライダー

    [Fact]
    public async Task Spec_Sliders_Should_Set_The_Cpu_And_Twice_The_Memory()
    {
        var h = await new Harness().ReadyAsync();
        var s = h.Main.Settings;
        var tt = s.TtSpec;
        (tt.Name, tt.MaxStep, tt.Step, tt.Text, tt.Note).Should().Be(("TimeTracker", 7, 5, "1.5 vCPU / 3 GiB", (string?)null));
        tt.Labels.Should().Equal("0.25", "0.5", "0.75", "1.0", "1.25", "1.5", "1.75", "2.0");
        var changes = new List<string?>();
        tt.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        tt.Step = 7;
        (s.TtCpu, s.TtMemoryGi, tt.Text).Should().Be(("2.0", "4.0", "2 vCPU / 4 GiB"));
        changes.Should().Contain([nameof(SpecSlider.Step), nameof(SpecSlider.Text), nameof(SpecSlider.Note)]);
        tt.Step = 99;
        s.TtCpu.Should().Be("2.0", "範囲の外は端に寄せる");
        tt.Step = -1;
        (s.TtCpu, s.TtMemoryGi).Should().Be(("0.25", "0.5"));

        // 組み合わせでない値は、その旨を出す。空の欄は既定値の段階
        s.DbMemoryGi = "3";
        s.DbSpec.Note.Should().StartWith("CPU とメモリが Container Apps の組み合わせ");
        s.RedisCpu = "0.6";
        (s.RedisSpec.Step, s.RedisSpec.Note).Should().Be((1, s.DbSpec.Note));
        s.RedisCpu = "";
        s.RedisMemoryGi = "";
        (s.RedisSpec.Step, s.RedisSpec.Text, s.RedisSpec.Note).Should().Be((1, "0.5 vCPU / 1 GiB", (string?)null));
        s.DbSpec.Step = 3;
        (s.DbCpu, s.DbMemoryGi, s.DbSpec.Note).Should().Be(("1.0", "2.0", (string?)null));
    }

    [Fact]
    public async Task Spec_Sliders_Should_Mark_The_Range_Below_The_Sample_Db_Minimum()
    {
        var h = await new Harness().ReadyAsync();
        var s = h.Main.Settings;
        var (tt, db, redis) = (s.TtSpec, s.DbSpec, s.RedisSpec);

        // 赤の帯: 最低スペックの段階まで（TimeTracker 1.25 は 4 / 7、DB 0.5 は 1 / 7、Redis 0.25 は帯なし）
        (tt.BelowMinimumShare, db.BelowMinimumShare, redis.BelowMinimumShare).Should().Be((4 / 7.0, 1 / 7.0, 0));
        Enumerable.Range(0, 8).Select(tt.IsBelowMinimumStep).Should().Equal(true, true, true, true, false, false, false, false);
        (tt.IsBelowMinimum, tt.MinimumText).Should().Be((false, "サンプル DB を作れる実用上の最低スペック: 1.25 vCPU / 2.5Gi"));

        var changes = new List<string?>();
        tt.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        tt.Step = 3;
        (tt.IsBelowMinimum, tt.MinimumText).Should().Be((true,
            "サンプル DB を作れる実用上の最低スペック（1.25 vCPU / 2.5Gi）を下回っています。サンプル DB の作成ができない場合があります。"));
        changes.Should().Contain([nameof(SpecSlider.IsBelowMinimum), nameof(SpecSlider.MinimumText)]);
        tt.Step = 4;
        tt.IsBelowMinimum.Should().BeFalse("最低スペックちょうどは赤にしない");
        s.TtMemoryGi = "2";
        tt.IsBelowMinimum.Should().BeTrue("CPU が足りていても、メモリが足りなければ下回る");
        db.Step = 0;
        db.IsBelowMinimum.Should().BeTrue();
        redis.Step = 0;
        redis.IsBelowMinimum.Should().BeFalse();
        (s.TtCpu, s.TtMemoryGi) = ("", "");
        tt.IsBelowMinimum.Should().BeFalse("空の欄は既定値（1.5 vCPU / 3Gi）");
    }

    // ---------------- 件数のカード・期限の残り・延長

    private static async Task<(Harness H, FakeTimeProvider Time)> ListedAsync()
    {
        var time = Tokyo();
        var h = new Harness(time: time);
        h.Azure.RecordsByTenant["t1"] =
        [
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "soon", "2026-11-15T14:59:59Z"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "late", "2027-03-01T14:59:59Z"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "none", "none"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "gone", "2026-11-10T14:59:59Z"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "bad", "2026-12-24T14:59:59Z", provisioning: "Failed"),
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg-old", "rg-old", ManagedResourceRecord.ResourceGroupType, "sub", "rg-old", "japaneast",
                ManagedTags.ForResourceGroup()),
        ];
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        return (h, time);
    }

    [Fact]
    public async Task The_Deployment_Cards_Should_Count_And_Filter()
    {
        var (h, _) = await ListedAsync();
        var list = h.Main.Deployments;
        (list.TotalCount, list.ExpiringSoonCount, list.AttentionCount, list.ActiveJobCount).Should().Be((6, 2, 2, 0));
        var changes = new List<string?>();
        list.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        list.QuickFilter = DeploymentQuickFilter.ExpiringSoon;
        list.Rows.Select(r => r.AppName).Should().BeEquivalentTo(["soon", "gone"]);
        list.QuickFilter = DeploymentQuickFilter.Attention;
        list.Rows.Select(r => r.AppName).Should().BeEquivalentTo(["bad", ""]);
        list.QuickFilter = DeploymentQuickFilter.Attention;
        list.QuickFilter = DeploymentQuickFilter.All;
        (list.QuickFilter, list.Rows.Count).Should().Be((DeploymentQuickFilter.All, 6));

        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "late"), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task);
        list.ActiveJobCount.Should().Be(1);
        changes.Should().Contain(nameof(DeploymentListViewModel.ActiveJobCount));
    }

    [Fact]
    public async Task Rows_Should_Show_The_Days_Left()
    {
        var (h, _) = await ListedAsync();
        var rows = h.Main.Deployments.Rows.ToDictionary(r => r.AppName);
        (rows["soon"].DaysLeft, rows["soon"].DaysLeftText, rows["soon"].ExpiryShare).Should().Be((3, "あと 3 日", 3 / 90.0));
        (rows["gone"].DaysLeft, rows["gone"].DaysLeftText, rows["gone"].ExpiryShare).Should().Be((0, "期限切れ", 0));
        (rows["late"].ExpiryShare, rows["none"].DaysLeft, rows["none"].DaysLeftText, rows["none"].ExpiryShare).Should().Be((1, (int?)null, (string?)null, 0));
    }

    [Theory]
    [InlineData("soon", 7, "2026-11-22T14:59:59Z")]
    [InlineData("soon", 30, "2026-12-15T14:59:59Z")]
    [InlineData("gone", 7, "2026-11-20T14:59:59Z")]
    [InlineData("none", 30, "2026-12-13T14:59:59Z")]
    public async Task Extending_Should_Add_Days_To_The_Expiry_Or_Today(string app, int days, string expected)
    {
        var (h, _) = await ListedAsync();
        var updated = new List<IReadOnlyDictionary<string, string>>();
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, _, t, _) => updated.Add(t))
            .Returns(Task.CompletedTask);
        h.Azure.Apps.Clear();
        foreach (var suffix in new[] { "tt", "redis", "db" })
            h.Azure.Apps.Add(new ContainerAppInfo($"{app}-{suffix}", FakeAzure.EnvId, ManagedTags.For(app)));
        var row = h.Main.Deployments.Rows.Single(r => r.AppName == app);

        await (days == 7 ? row.Extend7Command : row.Extend30Command).ExecuteAsync();

        row.Error.Should().BeNull();
        updated.Should().NotBeEmpty().And.OnlyContain(t => t[DeploymentExpiry.ExpiresAtKey] == expected);
    }

    [Fact]
    public async Task Extending_Should_Not_Be_Possible_While_A_Job_Runs_Or_Without_Apps()
    {
        var (h, _) = await ListedAsync();
        var rows = h.Main.Deployments.Rows.ToDictionary(r => r.AppName);
        (rows[""].Extend7Command.CanExecute(null), rows[""].Extend30Command.CanExecute(null)).Should().Be((false, false));
        var raised = 0;
        rows["late"].Extend7Command.CanExecuteChanged += (_, _) => raised++;
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "late"), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task);
        rows = h.Main.Deployments.Rows.ToDictionary(r => r.AppName);
        rows["late"].Extend30Command.CanExecute(null).Should().BeFalse();
        raised.Should().BePositive();
    }

    [Fact]
    public async Task The_Job_Cards_Should_Count_And_Filter()
    {
        var time = Tokyo();
        var history = new InMemoryJobHistory
        {
            Records =
            [
                new(JobKind.Deploy, "sub", "rg", "a", false, null, Now.AddDays(-1), DesktopJobState.Failed, "x", null, null, null, null, null, []),
                new(JobKind.Deploy, "sub", "rg", "b", false, null, Now.AddDays(-8), DesktopJobState.Failed, "x", null, null, null, null, null, []),
                new(JobKind.Deploy, "sub", "rg", "c", false, null, Now.AddDays(-2), DesktopJobState.Succeeded, null, null, null, null, null, null, []),
                new(JobKind.Delete, "sub", "rg", "d", false, null, Now.AddDays(-2), DesktopJobState.Canceled, null, null, null, null, null, null, []),
            ],
        };
        var h = await new Harness(time: time, history: history).ReadyAsync();
        var queue = h.Main.Queue;
        queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "e"), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task);
        queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "f"), null, () => new TaskCompletionSource<Job>().Task);
        var jobs = h.Main.JobList;
        (jobs.RunningCount, jobs.PendingCount, jobs.RecentFailedCount, jobs.RecentSucceededCount).Should().Be((1, 1, 1, 1));

        foreach (var (filter, expected) in new (JobQuickFilter, string[])[]
                 {
                     (JobQuickFilter.Running, ["rg / f"]), (JobQuickFilter.Pending, ["rg / e"]), (JobQuickFilter.RecentFailed, ["rg / a"]),
                     (JobQuickFilter.RecentSucceeded, ["rg / c"]), (JobQuickFilter.All, ["rg / a", "rg / b", "rg / c", "rg / d", "rg / e", "rg / f"]),
                 })
        {
            jobs.QuickFilter = filter;
            jobs.Items.Select(j => j.TargetText).Should().BeEquivalentTo(expected, filter.ToString());
        }
        jobs.QuickFilter = JobQuickFilter.All;
        jobs.QuickFilter.Should().Be(JobQuickFilter.All);
    }
}
