using FluentAssertions;
using Moq;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>利用可能期限日の判定に使う時刻（UTC+9 の時刻帯で 2026-10-22）。タイマーはシステムのものを使う。</summary>
internal sealed class TokyoTime : TimeProvider
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");

    public override DateTimeOffset GetUtcNow() => new(2026, 10, 21, 20, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => Zone;
}

public class ExpirySettingsTests
{
    private static async Task<Harness> ReadyAsync() => await new Harness(time: new TokyoTime()).ReadyAsync();

    [Fact]
    public async Task The_Default_Should_Leave_The_Expiry_To_The_Plan()
    {
        var vm = (await ReadyAsync()).Main.Settings;

        vm.Expiry.Should().Be(SettingsViewModel.ExpiryOptions[0]);
        vm.IsExpiryDate.Should().BeFalse();
        vm.ExpiryDate.Should().Be(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(9)));
        vm.ExpiryNotice.Should().StartWith("既定では、新しくデプロイする場合は 2026-11-23（今日から 32 日後）、再デプロイは今の期限を引き継ぎます。")
            .And.EndWith("期限を過ぎると、このアプリが自動で削除します（DB のデータも消えます）。");
        vm.Build(ValidationTarget.Azure).Options!.ExpiresOn.Should().BeNull();
        SettingsViewModel.ExpiryOptions.Select(o => o.Mode).Should().Equal(ExpiryMode.Default, ExpiryMode.Date, ExpiryMode.None);
    }

    [Fact]
    public async Task A_Date_Should_Be_Passed_As_Specified()
    {
        var vm = (await ReadyAsync()).Main.Settings;
        var changes = new ChangeRecorder(vm);

        vm.Expiry = SettingsViewModel.ExpiryOptions[1];
        vm.ExpiryDate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));

        changes.Names.Should().Contain(nameof(SettingsViewModel.IsExpiryDate)).And.Contain(nameof(SettingsViewModel.ExpiryDate));
        vm.IsExpiryDate.Should().BeTrue();
        vm.Errors.Should().BeEmpty();
        vm.Build(ValidationTarget.Azure).Options!.ExpiresOn.Should().Be("2026-12-01");
    }

    [Fact]
    public async Task Today_Should_Be_Allowed_But_A_Past_Date_Should_Be_An_Error()
    {
        var vm = (await ReadyAsync()).Main.Settings;
        vm.Expiry = SettingsViewModel.ExpiryOptions[1];

        vm.ExpiryDate = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.FromHours(9));
        vm.Errors.Should().BeEmpty();

        vm.ExpiryDate = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(9));
        vm.Errors.Should().Equal("利用可能期限日（2026-10-21）は、今日以降の日付を指定してください。");
        vm.Build(ValidationTarget.Azure).Options.Should().BeNull();
        // ローカル（docker compose）には期限が無いため、誤りにしない
        vm.Build(ValidationTarget.Compose).Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task No_Expiry_Should_Be_Passed_As_None()
    {
        var vm = (await ReadyAsync()).Main.Settings;

        vm.Expiry = SettingsViewModel.ExpiryOptions[2];

        vm.IsExpiryDate.Should().BeFalse();
        vm.Build(ValidationTarget.Azure).Options!.ExpiresOn.Should().Be(DeploymentExpiry.None);
    }

    [Fact]
    public async Task Choosing_The_Same_Values_Should_Not_Notify()
    {
        var vm = (await ReadyAsync()).Main.Settings;
        var changes = new ChangeRecorder(vm);

        vm.Expiry = SettingsViewModel.ExpiryOptions[0];
        vm.ExpiryDate = vm.ExpiryDate;

        changes.Names.Should().BeEmpty();
    }

    [Fact]
    public void The_System_Time_Should_Be_Used_When_Omitted()
    {
        var vm = new SettingsViewModel(new Harness().Main.Target);

        vm.ExpiryDate.Date.Should().Be(DateTime.Today.AddDays(DeploymentExpiry.DefaultDays));
    }
}

public class ExpiryDeploymentTests
{
    [Fact]
    public async Task The_Plan_And_The_Job_Should_Show_The_Default_Expiry()
    {
        var h = await new Harness(time: new TokyoTime()).ReadyAsync();
        h.Azure.ResourceGroup = null;
        var vm = h.Main.Deployment;
        var changes = new ChangeRecorder(vm);
        vm.ExpiryText.Should().BeNull();

        await vm.PlanCommand.ExecuteAsync();

        changes.Names.Should().Contain(nameof(DeploymentViewModel.ExpiryText));
        vm.ExpiryText.Should().Be("利用可能期限: 2026-11-23（既定: 今日から 32 日後）。期限の後に自動で削除します（DB のデータも消えます）。");

        DeployOptions? deployed = null;
        h.Azure.GatewayMock.Setup(g => g.CreateOrUpdateContainerAppAsync("sub", "rg", "demo", "japaneast", It.IsAny<DeployOptions>(),
                It.IsAny<Azure.ResourceManager.AppContainers.ContainerAppManagedEnvironmentResource>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string _, DeployOptions o, Azure.ResourceManager.AppContainers.ContainerAppManagedEnvironmentResource _, CancellationToken _) => deployed = o)
            .ReturnsAsync(new TTDeployTool.Infrastructure.Azure.ContainerAppsDeployment("demo-tt", "demo-db", "demo-redis", "https://demo-tt.example.io"));
        await vm.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        vm.ExpiryText.Should().BeNull();
        deployed!.LifecycleTags.Should().Contain(DeploymentExpiry.ExpiresAtKey, "2026-11-23T14:59:59Z");
        var job = h.Main.CurrentJob!;
        job.ExpirySummary.Should().Be("利用可能期限: " +
            new ExpiryDecision(new DateTimeOffset(2026, 11, 23, 14, 59, 59, TimeSpan.Zero), ExpirySource.Default).Describe(TimeZoneInfo.Local));
    }

    [Fact]
    public async Task No_Expiry_Should_Be_Shown_Without_The_Auto_Delete_Notice()
    {
        var h = await new Harness(time: new TokyoTime()).ReadyAsync();
        h.Azure.ResourceGroup = null;
        h.Main.Settings.Expiry = SettingsViewModel.ExpiryOptions[2];

        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        h.Main.Deployment.ExpiryText.Should().Be("利用可能期限: 期限なし");
    }

    [Fact]
    public async Task A_Redeployment_Should_Inherit_The_Current_Expiry()
    {
        var h = await new Harness(time: new TokyoTime()).ReadyAsync();
        h.Azure.Deployed();
        h.Azure.Apps[0] = h.Azure.Apps[0] with
        {
            Tags = new Dictionary<string, string>(h.Azure.Apps[0].Tags!) { [DeploymentExpiry.ExpiresAtKey] = "2026-10-31T14:59:59Z" },
        };

        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        h.Main.Deployment.ExpiryText.Should().StartWith("利用可能期限: 2026-10-31（今の期限を引き継ぎます）。");
    }
}
