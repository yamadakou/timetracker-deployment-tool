using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.Pricing;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 本物の Azure の公開の価格表（サインイン不要・無料）を読めること（No.88。オーナーの判断、2026-11-14: 案 A）。ほかのテストは偽の応答で確かめるため、
/// 価格表の形が変わったことには気付けない。環境変数 TT_LIVE_PRICES=1 のときだけ動く（CI の build ジョブの参考の手順。Azure 側の障害で CI を止めない）。
/// </summary>
public class LivePriceTests
{
    [Theory]
    [InlineData(PriceCurrency.Jpy)]
    [InlineData(PriceCurrency.Usd)]
    public async Task The_Public_Price_List_Should_Be_Read(PriceCurrency currency)
    {
        if (Environment.GetEnvironmentVariable("TT_LIVE_PRICES") != "1")
            Assert.Skip("TT_LIVE_PRICES=1 のときだけ、本物の価格表を読む");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var prices = await new AzureRetailPriceCatalog(http, TimeProvider.System).GetAsync(DeploymentCostBook.DefaultRegion, currency);

        prices.ReadAt.Should().NotBeNull("アプリに入れた価格ではなく、価格表から読んだ値");
        (prices.Currency, prices.Region).Should().Be((currency, DeploymentCostBook.DefaultRegion));
        prices.VcpuPerSecond.Should().BePositive();
        prices.GibPerSecond.Should().BePositive();
        prices.FilesPerGbMonth.Should().BePositive();
    }
}
