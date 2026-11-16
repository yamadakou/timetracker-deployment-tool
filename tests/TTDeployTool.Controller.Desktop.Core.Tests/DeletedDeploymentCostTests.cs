using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Inventory;
using TTDeployTool.Controller.Desktop.Core.Pricing;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 料金の目安の、削除したデプロイの分（オーナーの指摘、2026-11-16: 削除すると、削除した日までの料金が無かったことになると勘違いしやすい。
/// オーナーの判断: 案 1〔数える範囲を書き、Cost Management へ〕と案 2〔このアプリで削除したものを記録して、削除した日時までの分を数える〕）。
/// </summary>
public sealed class DeletedDeploymentCostTests
{
    // 東京の 11/13 09:00。今月は 11/01 00:00（10/31 15:00Z）から
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Tokyo()
    {
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TokyoTime.Zone);
        return time;
    }

    // TimeTracker だけ（1 vCPU / 2Gi: USD で 1 日 2.592）
    private static DeploymentEntry Entry(DateTimeOffset? deployed, string sub = "sub", string? location = "japaneast") =>
        new(sub, "rg", "demo", location, null, deployed, ExpiryKind.None, null, new AppEntry("demo-tt", "Succeeded", "Running", null, 1, "2Gi"), null, null, null, null, false);

    [Fact]
    public async Task A_Deleted_Deployment_Should_Cost_From_The_Deployment_To_The_Deletion()
    {
        var book = new DeploymentCostBook(new BuiltInPriceCatalog(), Tokyo()) { Currency = CostEstimateViewModel.CurrencyOptions[1] };
        var inMonth = new DeletedDeployment(Entry(Now.AddDays(-10)), Now.AddDays(-2));
        // 先月の 10/01 にデプロイし、今月の 11/05 00:00Z に削除: 今月分は 10/31 15:00Z からの 4.375 日
        var fromLastMonth = new DeletedDeployment(Entry(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)), new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero));
        var lastMonth = new DeletedDeployment(Entry(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)), new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        var unknown = new DeletedDeployment(Entry(null), Now.AddDays(-1));
        var otherRegion = new DeletedDeployment(Entry(Now.AddDays(-3), location: "westus"), Now.AddDays(-1));
        await book.LoadAsync(DeploymentCostBook.PricedEntries([], [inMonth, fromLastMonth, lastMonth, unknown]));

        var cost = book.ForDeleted(inMonth)!;
        (cost.Text, cost.PerDayText, Money.Text(cost.ThisMonth, PriceCurrency.Usd)).Should().Be(("約 $20.74", "1 日 約 $2.59", "$20.74"));
        var spanning = book.ForDeleted(fromLastMonth)!;
        (Money.Text(spanning.Total, PriceCurrency.Usd), Money.Text(spanning.ThisMonth, PriceCurrency.Usd)).Should().Be(("$90.72", "$11.34"));
        book.ForDeleted(unknown).Should().BeNull("デプロイの日時が分からなければ数えない");
        book.ForDeleted(otherRegion).Should().BeNull("単価をまだ読んでいない");

        // 今月に削除したものだけを数え、今月分に足す
        IReadOnlyList<DeletedDeployment> all = [inMonth, fromLastMonth, lastMonth, unknown, otherRegion];
        (book.DeletedText(all), book.DeletedNote(all)).Should().Be(("約 $32.08", "今月このアプリで削除した 2 件の、削除した日時までの分（今月分に含みます）"));
        book.ThisMonthText([], all).Should().Be("約 $32.08");
        (book.DeletedText([lastMonth]), book.DeletedNote([lastMonth]), book.ThisMonthText([], [lastMonth])).Should().Be(((string?)null, (string?)null, (string?)null));
    }

    [Fact]
    public async Task Deleting_From_This_App_Should_Record_The_Deleted_Part_And_Count_It()
    {
        var time = Tokyo();
        var h = await new Harness(time: time).ReadyAsync();
        h.Azure.Deployed();
        h.Azure.RecordsByTenant["t1"] = [.. DeploymentListViewModelTests.Deployment("sub", "rg", "demo", "none", deployed: "2026-11-10T00:00:00Z")];
        var list = h.Main.Deployments;
        await list.LoadCommand.ExecuteAsync();
        var before = list.CostThisMonthText;
        (list.CostDeletedText, list.CostDeletedNote).Should().Be(((string?)null, (string?)null), "まだ削除していない");
        list.CostScopeNote.Should().StartWith("「全体」は今あるデプロイだけを数えます。");

        // 古い記録（180 日より前）は、次に記録するときに片付ける
        h.Preferences.DeletedDeployments = [new DeletedDeployment(Entry(Now.AddDays(-200)), Now.AddDays(-190))];
        h.Main.Target.AppName = "demo";
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        h.Main.Deletion.ConfirmAppName = "demo";
        time.Advance(TimeSpan.FromHours(1));
        await h.Main.Deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        var recorded = h.Preferences.DeletedDeployments.Should().ContainSingle().Subject;
        (recorded.DeletedAt, recorded.Entry.AppName, recorded.Entry.Timetracker?.Name, recorded.Entry.Db?.Name, recorded.Entry.Redis?.Name, recorded.Entry.Url)
            .Should().Be((Now.AddHours(1), "demo", "demo-tt", "demo-db", "demo-redis", (string?)null));
        list.Rows.Should().BeEmpty("削除したものは一覧から消える");
        list.CostDeletedText.Should().StartWith("約 ").And.EndWith(" 円");
        list.CostDeletedNote.Should().Be("今月このアプリで削除した 1 件の、削除した日時までの分（今月分に含みます）");
        list.CostThisMonthText.Should().Be(list.CostDeletedText, "今あるデプロイが無くなり、削除した分だけが今月分に残る");
        list.CostThisMonthText.Should().NotBe(before);

        // 読めないサブスクリプションの記録は数えない（別のアカウントで削除したもの）
        h.Preferences.DeletedDeployments = [new DeletedDeployment(Entry(Now.AddDays(-3), sub: "other"), Now.AddDays(-1))];
        list.DeletedDeployments.Should().BeEmpty();
        // 残す期間（180 日）を過ぎた記録も数えない
        h.Preferences.DeletedDeployments = [new DeletedDeployment(Entry(Now.AddDays(-200)), Now.AddDays(-181))];
        list.DeletedDeployments.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_The_Deleted_Part_Should_Be_Recorded()
    {
        var h = await new Harness(time: Tokyo()).ReadyAsync();
        var list = h.Main.Deployments;
        var entry = Entry(Now.AddDays(-3)) with { Db = new AppEntry("demo-db", null, null, null, 1, "2Gi"), StorageAccountName = "st", DbPersisted = true };
        void Record(DeploymentEntry? e, string[] gone, bool rg) => list.RecordDeleted(e, gone.ToHashSet(StringComparer.OrdinalIgnoreCase), rg);

        // DB のデータを残した削除: Container App だけ。何も消していない・リソースグループだけ・行が無いものは記録しない
        Record(entry, ["demo-tt", "demo-db"], false);
        var kept = h.Preferences.DeletedDeployments.Single().Entry;
        (kept.Timetracker?.Name, kept.Db?.Name, kept.DbPersisted).Should().Be(("demo-tt", "demo-db", false));
        Record(entry, [], false);
        Record(entry with { AppName = "" }, [], true);
        Record(null, ["demo-tt"], false);
        Record(entry with { StorageAccountName = null }, [], false);
        h.Preferences.DeletedDeployments.Should().HaveCount(1);
        // リソースグループごと消したなら、すべて（DB のデータも）
        Record(entry, [], true);
        h.Preferences.DeletedDeployments[^1].Entry.DbPersisted.Should().BeTrue();

        // 上限の件数を超えたら、古いものから外す
        h.Preferences.DeletedDeployments = [.. Enumerable.Range(0, DeploymentListViewModel.DeletedLimit).Select(i => new DeletedDeployment(entry with { AppName = $"a{i}" }, Now))];
        Record(entry, ["demo-tt"], false);
        (h.Preferences.DeletedDeployments.Count, h.Preferences.DeletedDeployments[0].Entry.AppName).Should().Be((DeploymentListViewModel.DeletedLimit, "a1"));
    }

    [Fact]
    public void Deleted_Deployments_Should_Be_Saved_In_The_Preferences_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tt-deleted-{Guid.NewGuid():N}.json");
        try
        {
            var deleted = new DeletedDeployment(Entry(Now.AddDays(-3)), Now);
            new FilePreferences(path).DeletedDeployments = [deleted];
            var read = new FilePreferences(path).DeletedDeployments.Single();
            (read.DeletedAt, read.Entry.DeployedAt, read.Entry.Timetracker, read.Entry.Location).Should().Be((deleted.DeletedAt, deleted.Entry.DeployedAt, deleted.Entry.Timetracker, "japaneast"));
            new FilePreferences(Path.Combine(Path.GetTempPath(), $"tt-none-{Guid.NewGuid():N}.json")).DeletedDeployments.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cost_Management_Should_Open_In_The_Signed_In_Tenant()
    {
        var h = new Harness();
        h.Main.OpenCostManagement();
        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        h.Main.OpenCostManagement();
        h.Launcher.Opened.Should().Equal(
            "https://portal.azure.com/#view/Microsoft_Azure_CostManagement/Menu/~/overview",
            "https://portal.azure.com/#@home/view/Microsoft_Azure_CostManagement/Menu/~/overview");
    }
}
