using FluentAssertions;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// デプロイの一覧のページの境界（オーナーの依頼、2026-11-02: 境界値は単体テストで確かめる）。20 件ずつのとき、0・1・20・21・40・41 件で、
/// ページの数・最後のページの件数・前後のページへ移れるか・表示の文を確かめる。50・100 件ずつの境目も確かめる。
/// </summary>
public sealed class PageBoundaryTests
{
    private readonly MovableTime _time = new();

    private async Task<Harness> LoadedAsync(int count)
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.RecordsByTenant["t1"] = [.. Enumerable.Range(1, count).SelectMany(i => DeploymentListViewModelTests.Deployment("sub", "rg", $"app{i:000}"))];
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        return h;
    }

    /// <summary>件数・ページの数・最後のページの件数・最後のページの表示。</summary>
    public static TheoryData<int, int, int, string> Twenties => new()
    {
        { 1, 1, 1, "1〜1 件目を表示（全 1 件）・1 / 1 ページ" },
        { 20, 1, 20, "1〜20 件目を表示（全 20 件）・1 / 1 ページ" },
        { 21, 2, 1, "21〜21 件目を表示（全 21 件）・2 / 2 ページ" },
        { 40, 2, 20, "21〜40 件目を表示（全 40 件）・2 / 2 ページ" },
        { 41, 3, 1, "41〜41 件目を表示（全 41 件）・3 / 3 ページ" },
    };

    [Theory]
    [MemberData(nameof(Twenties))]
    public async Task Twenty_Rows_A_Page_Should_Split_At_Twenty(int count, int pages, int lastRows, string lastText)
    {
        var vm = (await LoadedAsync(count)).Main.Deployments;
        vm.PageCount.Should().Be(pages);
        vm.Rows.Should().HaveCount(Math.Min(count, 20));
        vm.PreviousPageCommand.CanExecute(null).Should().BeFalse("最初のページ");
        for (var i = 1; i < pages; i++)
        {
            vm.NextPageCommand.CanExecute(null).Should().BeTrue();
            await vm.NextPageCommand.ExecuteAsync();
        }
        vm.Rows.Should().HaveCount(lastRows);
        vm.PageText.Should().Be(lastText);
        vm.NextPageCommand.CanExecute(null).Should().BeFalse("最後のページ");
        vm.PreviousPageCommand.CanExecute(null).Should().Be(pages > 1);
    }

    [Fact]
    public async Task No_Rows_Should_Have_One_Empty_Page()
    {
        var vm = (await LoadedAsync(0)).Main.Deployments;
        vm.Rows.Should().BeEmpty();
        vm.PageText.Should().Be("表示するデプロイはありません（全 0 件）。");
        vm.NextPageCommand.CanExecute(null).Should().BeFalse();
        vm.PreviousPageCommand.CanExecute(null).Should().BeFalse();
    }

    [Theory]
    [InlineData(50, 50, 1)]
    [InlineData(50, 51, 2)]
    [InlineData(100, 100, 1)]
    [InlineData(100, 101, 2)]
    public async Task Larger_Pages_Should_Split_At_Their_Size(int size, int count, int pages)
    {
        var vm = (await LoadedAsync(count)).Main.Deployments;
        vm.PageSize = size;
        vm.PageCount.Should().Be(pages);
        vm.Rows.Should().HaveCount(Math.Min(count, size));
    }
}
