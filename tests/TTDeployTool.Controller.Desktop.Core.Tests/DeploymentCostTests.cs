using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Controller.Desktop.Core.Pricing;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>デプロイの一覧の料金の目安（オーナーの依頼、2026-11-14）と、まとめての削除のリソースグループだけの行（オーナーの指摘、2026-11-14）。</summary>
public sealed class DeploymentCostTests
{
    // 東京の 11/13 09:00。今月は 11/01 00:00（10/31 15:00Z）から 12/01 00:00（11/30 15:00Z）
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Tokyo()
    {
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TokyoTime.Zone);
        return time;
    }

    // TimeTracker だけ（1 vCPU / 2Gi: USD で 1 秒 0.00003、1 日 2.592）
    private static DeploymentEntry Entry(
        DateTimeOffset? deployed, ExpiryKind kind, DateTimeOffset? expires, string app = "demo", double? cpu = 1, string? memory = "2Gi", string? location = "japaneast") =>
        new("sub", "rg", app, location, null, deployed, kind, expires,
            new AppEntry(app + "-tt", "Succeeded", "Running", null, cpu, memory), null, null, $"{app}-env", null, false);

    private static async Task<DeploymentCostBook> UsdAsync(params DeploymentEntry[] entries)
    {
        var book = new DeploymentCostBook(new BuiltInPriceCatalog(), Tokyo()) { Currency = CostEstimateViewModel.CurrencyOptions[1] };
        await book.LoadAsync(entries);
        return book;
    }

    [Fact]
    public async Task A_Deployment_With_An_Expiry_Should_Cost_From_The_Deployment_To_The_Expiry()
    {
        var entry = Entry(Now.AddDays(-10), ExpiryKind.Date, Now.AddDays(10));
        var cost = (await UsdAsync(entry)).For(entry)!;

        (cost.Text, cost.PerDayText, cost.IsMonthly).Should().Be(("約 $51.84", "1 日 約 $2.59", false));
        Money.Text(cost.ThisMonth, PriceCurrency.Usd).Should().Be("$51.84", "今月の中で始まり、今月の中で終わる");
    }

    [Fact]
    public async Task A_Deployment_Without_An_Expiry_Should_Cost_A_Month_And_Count_The_Whole_Month()
    {
        var none = Entry(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ExpiryKind.None, null);
        var missing = Entry(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ExpiryKind.Missing, null, app: "old");
        var book = await UsdAsync(none, missing);

        foreach (var entry in new[] { none, missing })
        {
            var cost = book.For(entry)!;
            (cost.Text, cost.IsMonthly).Should().Be(("1 か月 約 $77.76", true));
            Money.Text(cost.ThisMonth, PriceCurrency.Usd).Should().Be("$77.76", "先月から動いているため、今月の 1 日から月末まで（30 日）");
        }
    }

    [Fact]
    public async Task Without_A_Deployment_Time_It_Should_Start_Now_And_End_At_The_Month_End()
    {
        var entry = Entry(null, ExpiryKind.Date, Now.AddDays(30));
        var cost = (await UsdAsync(entry)).For(entry)!;

        cost.Text.Should().Be("約 $77.76");
        Money.Text(cost.ThisMonth, PriceCurrency.Usd).Should().Be("$45.68", "今から月末（11/30 15:00Z）までの 17.625 日");
    }

    [Fact]
    public async Task An_Expired_Deployment_That_Still_Exists_Should_Cost_Until_Now()
    {
        var entry = Entry(Now.AddDays(-10), ExpiryKind.Date, Now.AddDays(-3));
        var cost = (await UsdAsync(entry)).For(entry)!;

        cost.Text.Should().Be("約 $25.92");
        Money.Text(cost.ThisMonth, PriceCurrency.Usd).Should().Be("$25.92");
    }

    [Fact]
    public async Task Unknown_Specs_Should_Use_The_Defaults_And_Persistence_Should_Add_Azure_Files()
    {
        // 既定の TimeTracker（1.5 vCPU / 3Gi: 1 日 3.888）、読めないメモリの DB（1 vCPU と既定の 2Gi: 1 日 2.592）、永続化 1 GB（30 日で 0.03）
        var entry = Entry(null, ExpiryKind.None, null, cpu: null, memory: null, location: null) with
        {
            Db = new AppEntry("demo-db", null, null, null, 1, "?"),
            DbPersisted = true,
        };
        var book = await UsdAsync(entry);

        book.For(entry)!.Text.Should().Be("1 か月 約 $194.43", "(3.888 + 2.592) × 30 + 0.03");
    }

    [Fact]
    public async Task Rows_Without_Prices_Or_Apps_And_Resource_Group_Only_Rows_Should_Not_Be_Estimated()
    {
        var group = Entry(null, ExpiryKind.Missing, null, app: "") with { Timetracker = null };
        var empty = Entry(null, ExpiryKind.Missing, null) with { Timetracker = null };
        var elsewhere = Entry(null, ExpiryKind.None, null, location: "westus");
        var book = await UsdAsync(group, empty);

        (book.For(group), book.For(empty), book.For(elsewhere)).Should().Be(((DeploymentCost?)null, (DeploymentCost?)null, (DeploymentCost?)null));
        (book.TotalText([group]), book.TotalNote([group]), book.ThisMonthText([group])).Should().Be(((string?)null, (string?)null, (string?)null));
        (await book.LoadAsync([elsewhere])).Should().BeTrue("新しいリージョン");
        (await book.LoadAsync([elsewhere])).Should().BeFalse("読んだリージョンは読み直さない");
        book.For(elsewhere).Should().NotBeNull();
    }

    [Fact]
    public async Task The_Totals_Should_Sum_The_Rows_And_Say_How_Rows_Without_An_Expiry_Are_Counted()
    {
        var dated = Entry(Now.AddDays(-10), ExpiryKind.Date, Now.AddDays(10));
        var none = Entry(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ExpiryKind.None, null, app: "none");
        var book = await UsdAsync(dated, none);

        (book.TotalText([dated, none]), book.TotalNote([dated, none]), book.ThisMonthText([dated, none]), book.ThisMonthLabel)
            .Should().Be(("約 $129.60", "2 件のデプロイから期限まで（期限なしの 1 件は 1 か月分）", "約 $129.60", "11 月の見込み"));
        book.TotalNote([dated]).Should().Be("1 件のデプロイから期限まで");
    }

    [Fact]
    public async Task A_Blank_Region_Should_Use_The_Default_One_And_A_Future_Deployment_Time_Should_Count_Nothing()
    {
        // 時計のずれで、デプロイの日時が今より後・期限がその前になっていても、負の料金にしない
        var entry = Entry(Now.AddDays(1), ExpiryKind.Date, Now.AddHours(12), location: "");
        var book = await UsdAsync(entry);

        (book.For(entry)!.Total, book.For(entry)!.ThisMonth).Should().Be((0m, 0m));
        book.Currency.Should().Be(CostEstimateViewModel.CurrencyOptions[1]);
    }

    [Fact]
    public void Choosing_The_Same_Currency_Should_Not_Reload()
    {
        var book = new DeploymentCostBook(new BuiltInPriceCatalog(), Tokyo());
        var changed = 0;
        book.CurrencyChanged += () => changed++;

        book.Currency = CostEstimateViewModel.CurrencyOptions[0];
        book.Currency = CostEstimateViewModel.CurrencyOptions[1];

        changed.Should().Be(1);
    }

    // ---------------- デプロイの一覧

    private static async Task<Harness> ListedAsync()
    {
        var h = new Harness(time: Tokyo());
        h.Azure.RecordsByTenant["t1"] =
        [
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "small", "2026-11-15T14:59:59Z", deployed: "2026-11-12T00:00:00Z"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "large", "2026-12-24T14:59:59Z", deployed: "2026-11-01T00:00:00Z"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "forever", "none", deployed: "2026-11-10T00:00:00Z"),
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg-old", "rg-old", ManagedResourceRecord.ResourceGroupType, "sub", "rg-old", "japaneast",
                ManagedTags.ForResourceGroup()),
        ];
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        return h;
    }

    [Fact]
    public async Task The_List_Should_Show_The_Cost_Of_Each_Row_And_The_Totals_In_Yen()
    {
        var list = (await ListedAsync()).Main.Deployments;
        var rows = list.Rows.ToDictionary(r => r.AppName);

        rows["small"].CostText.Should().StartWith("約 ").And.EndWith(" 円");
        rows["small"].CostPerDayText.Should().StartWith("1 日 約 ");
        rows["forever"].CostText.Should().StartWith("1 か月 約 ");
        (rows[""].CostText, rows[""].CostPerDayText).Should().Be(((string?)null, (string?)null), "リソースグループだけの行は見積もらない");
        list.Filters.Columns.Single(c => c.Key == "Cost").TextOf(rows[""].Entry).Should().Be("見積もれません");
        list.CostTotalText.Should().StartWith("約 ").And.EndWith(" 円");
        list.CostTotalNote.Should().Be("3 件のデプロイから期限まで（期限なしの 1 件は 1 か月分）");
        (list.CostThisMonthText, list.CostThisMonthLabel).Should().Match<(string?, string)>(x => x.Item1!.EndsWith(" 円") && x.Item2 == "11 月の見込み");
    }

    [Fact]
    public async Task The_List_Should_Sort_By_The_Cost_And_Switch_The_Currency()
    {
        var list = (await ListedAsync()).Main.Deployments;
        list.SortBy(DeploymentSortKey.Cost);
        list.Rows.Select(r => r.AppName).Should().Equal(["small", "forever", "large", ""], "安い順。見積もれない行は最後");
        list.SortBy(DeploymentSortKey.Cost);
        list.Rows.Select(r => r.AppName).Should().Equal(["large", "forever", "small", ""]);

        var row = list.Rows.First();
        var rowChanges = new List<string?>();
        row.PropertyChanged += (_, e) => rowChanges.Add(e.PropertyName);
        list.Costs.Currency = CostEstimateViewModel.CurrencyOptions[1];
        await Task.Yield();

        list.CostTotalText.Should().StartWith("約 $");
        list.Rows.First().CostText.Should().StartWith("約 $");

        // 読んだ単位に戻すと、読み直さずに表示し直す
        var changes = new List<string?>();
        list.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        row = list.Rows.First();
        row.PropertyChanged += (_, e) => rowChanges.Add(e.PropertyName);
        list.Costs.Currency = CostEstimateViewModel.CurrencyOptions[0];

        list.CostTotalText.Should().EndWith(" 円");
        changes.Should().Contain([nameof(DeploymentListViewModel.CostTotalText), nameof(DeploymentListViewModel.CostThisMonthText)]);
        rowChanges.Should().Contain(nameof(DeploymentRowViewModel.CostText));
    }

    [Theory]
    [InlineData("sort")]
    [InlineData("column")]
    [InlineData("text")]
    public async Task Rows_Sorted_Or_Filtered_By_The_Cost_Should_Be_Rebuilt_When_Prices_Arrive(string how)
    {
        var list = (await ListedAsync()).Main.Deployments;
        var forever = list.Rows.Single(r => r.AppName == "forever").CostText!;
        switch (how)
        {
            case "sort": list.SortBy(DeploymentSortKey.Cost); break;
            case "column": list.Filters.SetValues("Cost", [forever]); break;
            default: list.Filters.Text = "1 か月"; break;
        }
        list.Costs.Currency = CostEstimateViewModel.CurrencyOptions[1];

        // 単価を読み直した後の値で、並べ替え・絞り込みをし直す（円の値で絞り込んでいれば、USD の行は合わない）
        list.Rows.Select(r => r.CostText).Should().OnlyContain(t => t == null || t.Contains('$'));
        if (how == "column")
            list.Rows.Should().BeEmpty();
        if (how == "text")
            list.Rows.Select(r => r.AppName).Should().Equal("forever");
    }

    [Fact]
    public async Task The_Total_Should_Say_It_Is_Calculating_Until_The_Prices_Arrive_And_When_Nothing_Can_Be_Estimated()
    {
        var prices = new TaskCompletionSource<UnitPrices>();
        var catalog = new Moq.Mock<IPriceCatalog>();
        catalog.Setup(c => c.GetAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<PriceCurrency>(), Moq.It.IsAny<CancellationToken>())).Returns(prices.Task);
        var h = new Harness(time: Tokyo(), prices: catalog.Object);
        h.Azure.RecordsByTenant["t1"] =
        [
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg-old", "rg-old", ManagedResourceRecord.ResourceGroupType, "sub", "rg-old", "japaneast",
                ManagedTags.ForResourceGroup()),
        ];
        await h.ReadyAsync();
        var list = h.Main.Deployments;
        await list.LoadCommand.ExecuteAsync();
        list.CostTotalDisplay.Should().Be("計算しています");

        prices.SetResult(BuiltInPriceCatalog.Jpy);
        await Task.Yield();
        list.CostTotalDisplay.Should().Be("—（見積もれる行がありません）", "リソースグループだけの行は見積もらない");
    }

    [Fact]
    public async Task Rows_Should_Keep_Two_Lines_While_The_Prices_Are_Read()
    {
        // 単価を読む前も 2 行で出す（後から 2 行目が加わると、行が高くなって下の行がずれた。2026-11-16）
        var prices = new TaskCompletionSource<UnitPrices>();
        var catalog = new Moq.Mock<IPriceCatalog>();
        catalog.Setup(c => c.GetAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<PriceCurrency>(), Moq.It.IsAny<CancellationToken>())).Returns(prices.Task);
        var h = new Harness(time: Tokyo(), prices: catalog.Object);
        h.Azure.RecordsByTenant["t1"] =
        [
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "demo", "2026-11-20T14:59:59Z"),
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg-old", "rg-old", ManagedResourceRecord.ResourceGroupType, "sub", "rg-old", "japaneast",
                ManagedTags.ForResourceGroup()),
        ];
        await h.ReadyAsync();
        var list = h.Main.Deployments;
        await list.LoadCommand.ExecuteAsync();
        var rows = list.Rows.ToDictionary(r => r.AppName);
        (rows["demo"].CostText, rows["demo"].CostPerDayText).Should().Be(("計算しています", "1 日 —"));
        (rows[""].CostText, rows[""].CostPerDayText).Should().Be(((string?)null, (string?)null), "リソースグループだけの行は見積もらない");

        prices.SetResult(BuiltInPriceCatalog.Jpy);
        await Task.Yield();
        rows["demo"].CostText.Should().StartWith("約 ").And.EndWith(" 円");
        rows["demo"].CostPerDayText.Should().StartWith("1 日 約 ");
    }

    [Fact]
    public async Task The_Total_Display_Should_Be_The_Total_When_Estimated()
    {
        var list = (await ListedAsync()).Main.Deployments;
        list.CostTotalDisplay.Should().Be(list.CostTotalText);
    }

    // ---------------- まとめての削除のリソースグループだけの行

    [Fact]
    public async Task A_Batch_Of_Only_Resource_Groups_Should_Not_Offer_The_Options_And_Should_Say_The_Group_Is_Deleted()
    {
        var h = new Harness(time: Tokyo());
        h.Azure.RecordsByTenant["t1"] =
        [
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg", "rg", ManagedResourceRecord.ResourceGroupType, "sub", "rg", "japaneast", ManagedTags.ForResourceGroup()),
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg2", "rg2", ManagedResourceRecord.ResourceGroupType, "sub", "rg2", "japaneast", ManagedTags.ForResourceGroup()),
        ];
        h.Azure.ResourceGroup = new ResourceGroupInfo("rg", "japaneast", ManagedTags.ForResourceGroup());
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        var vm = h.Main.BatchDeletion;
        vm.IsAllResourceGroupOnly.Should().BeFalse("まだ選んでいない");

        await vm.OpenAsync(h.Main.Deployments.Entries);

        (vm.IsAllResourceGroupOnly, vm.ResourceGroupOnlyText).Should().Be((true,
            "選んだのは、本ツールのアプリのリソースが無い、リソースグループだけの行です。リソースグループを削除します（中にほかのリソースがあれば削除しません。元に戻せません）。"));
        vm.Items.Select(i => (i.Title, i.Summary)).Should().Contain(("rg（リソースグループだけ）", "リソースグループ rg を削除します（中にほかのリソースがあれば削除しません。元に戻せません）。"));
    }

    [Fact]
    public async Task A_Mixed_Batch_Should_Say_The_Resource_Group_Only_Rows_Ignore_The_Options()
    {
        var h = new Harness(time: Tokyo());
        h.Azure.RecordsByTenant["t1"] =
        [
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "demo"),
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg-old", "rg-old", ManagedResourceRecord.ResourceGroupType, "sub", "rg-old", "japaneast",
                ManagedTags.ForResourceGroup()),
        ];
        h.Azure.Deployed();
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        var vm = h.Main.BatchDeletion;
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await vm.OpenAsync(h.Main.Deployments.Entries);

        (vm.IsAllResourceGroupOnly, vm.ResourceGroupOnlyText).Should().Be((false,
            "リソースグループだけの行（1 件）は、下の指定にかかわらず、リソースグループを削除します（中にほかのリソースがあれば削除しません）。"));
        vm.ConfirmCount = vm.RunnableCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await vm.StartCommand.ExecuteAsync();

        (vm.IsAllResourceGroupOnly, vm.ResourceGroupOnlyText).Should().Be((false, (string?)null), "登録した後は、選んだものを外す");
        changes.Should().Contain(nameof(BatchDeletionViewModel.ResourceGroupOnlyText));
    }
}
