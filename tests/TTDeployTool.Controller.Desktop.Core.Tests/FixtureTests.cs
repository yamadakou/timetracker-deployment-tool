using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>UI テストのモック（docs/design/desktop/desktop-ui-tests.md の 6.2）。本物の画面の状態（MainViewModel）に渡して確かめる。</summary>
public class FixtureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 3, 0, 0, TimeSpan.Zero);

    private static FixtureData Data => FixtureSamples.Data(25, Now);

    [Fact]
    public void The_Data_Should_Survive_The_File()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tt-fixture-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, FixtureData.FileName);
        try
        {
            FixtureData.Load(path).Should().BeNull("ファイルが無ければモックのデータは無い");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, Data.ToJson());
            var loaded = FixtureData.Load(path)!;
            loaded.Account.Should().Be(new SignedInAccount("mock-user@example.com", FixtureSamples.TenantId));
            loaded.Resources.Should().HaveCount(100, "デプロイごとに Environment と 3 つの Container App");
            loaded.Resources[1].App!.RunningStatus.Should().Be("Running");
            loaded.Secrets!.Should().HaveCount(25);
            File.WriteAllText(path, "{");
            FluentActions.Invoking(() => FixtureData.Load(path)).Should().Throw<System.Text.Json.JsonException>("読めないデータはテストの誤り");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_Samples_Should_Mix_Subscriptions_Groups_Expiry_And_States()
    {
        var data = Data;
        data.Subscriptions.Select(s => s.SubscriptionId).Should().Equal(FixtureSamples.SubscriptionA, FixtureSamples.SubscriptionB);
        var apps = data.Resources.Where(r => r.Name.EndsWith("-tt", StringComparison.Ordinal)).ToList();
        apps.Select(a => a.Tags.GetValueOrDefault(DeploymentGroup.Key)).Distinct().Should().BeEquivalentTo(["研修", "営業", null]);
        apps.Select(a => a.Tags[DeploymentExpiry.ExpiresAtKey]).Should().Contain(DeploymentExpiry.None).And.Contain(DeploymentExpiry.FormatTag(Now.AddDays(3)));
        apps.Count(a => a.App!.RunningStatus == "Stopped").Should().Be(5);

        var records = FixtureSamples.JobRecords(45, Now);
        records.Should().HaveCount(45);
        records.Select(r => r.State).Distinct().Should().BeEquivalentTo([DesktopJobState.Succeeded, DesktopJobState.Failed, DesktopJobState.Canceled, DesktopJobState.Stopped]);
        records.Select(r => r.Kind).Distinct().Should().BeEquivalentTo([JobKind.Deploy, JobKind.Delete]);
        records[0].Error.Should().Be(FixtureSamples.LongText);
        records[5].Error.Should().Be("権限がありません。");
        records[4].Error.Should().BeNull();
        records.Last().FinishedAt.Should().Be(Now.AddHours(-1));
    }

    [Fact]
    public async Task The_Sign_In_Should_Use_The_Data_And_Never_Reach_Azure()
    {
        FixtureData? data = null;
        var signIn = new FixtureSignInService(() => data);
        (await signIn.RestoreAsync()).Should().BeNull("起動したときはサインインしていない");
        await FluentActions.Awaiting(() => signIn.SignInAsync()).Should().ThrowAsync<InvalidOperationException>().WithMessage("*uitest-fixture.json*");
        FluentActions.Invoking(() => signIn.Credential).Should().Throw<InvalidOperationException>();

        data = Data;
        (await signIn.SignInAsync()).UserName.Should().Be("mock-user@example.com");
        var credential = signIn.Credential;
        FluentActions.Invoking(() => credential.GetToken(new TokenRequestContext(["https://management.azure.com/.default"]), default))
            .Should().Throw<InvalidOperationException>();
        await FluentActions.Awaiting(async () => await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), default))
            .Should().ThrowAsync<InvalidOperationException>();
        await signIn.SignOutAsync();
        FluentActions.Invoking(() => signIn.Credential).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task The_App_Should_List_The_Deployments_And_Keep_A_Deletion_Running_Until_Stopped()
    {
        // 書き込み 1 回に 0.3 秒（実行中のまま見せ、停止は次の手順の境目で効く）
        var data = Data with { WriteDelaySeconds = 0.3 };
        var main = new MainViewModel(new FixtureSignInService(() => data), new FixtureAzureClients(data), new FakeDialogs(), new FakeLauncher(), TimeProvider.System,
            NullLoggerFactory.Instance, TimeSpan.FromMilliseconds(5), new InMemoryAppPreferences(), null, new FakeClipboard(), null, null, new InMemoryJobHistory());

        await main.SignIn.SignInCommand.ExecuteAsync();
        main.Target.Subscriptions.Select(s => s.SubscriptionId).Should().BeEquivalentTo([FixtureSamples.SubscriptionA, FixtureSamples.SubscriptionB]);
        await main.Deployments.LoadCommand.ExecuteAsync();
        main.Deployments.Error.Should().BeNull();
        main.Deployments.PageText.Should().Contain("全 25 件");
        main.Deployments.Rows.Should().HaveCount(20, "20 件ずつ");

        // パスワードは、モックのシークレットから読む
        var row = main.Deployments.Rows.First(r => r.AppName == "app00");
        await row.ShowPasswordsCommand.ExecuteAsync();
        await row.TogglePasswordsCommand.ExecuteAsync();
        row.DbPasswordText.Should().Be("Db-Pass-00");

        // 削除は、本物の計画を通り、書き込みで止まって実行中のまま残る。停止で止まる
        main.Target.SelectedSubscription = main.Target.Subscriptions.First(s => s.SubscriptionId == FixtureSamples.SubscriptionA);
        main.Target.ResourceGroup = "rg-mock-00";
        main.Target.AppName = "app00";
        var deletion = main.Deletion;
        deletion.DeleteResourceGroup = true;
        await deletion.PlanCommand.ExecuteAsync();
        deletion.Error.Should().BeNull();
        deletion.Targets.Where(t => t.Decision == "削除する").Select(t => t.Name).Should().BeEquivalentTo(["app00-tt", "app00-db", "app00-redis", "app00-env", "rg-mock-00"], "本ツールで作った・ほかのリソースが無いリソースグループも消せる");
        deletion.ConfirmAppName = "app00";
        await deletion.StartCommand.ExecuteAsync();
        var job = main.CurrentJob!;
        await WaitAsync(() => job.State == DesktopJobState.Running, "削除が実行中になる");
        await job.StopCommand.ExecuteAsync();
        await WaitAsync(() => job.State == DesktopJobState.Stopped, "停止で止まる");
        main.Dispose();
    }

    [Fact]
    public async Task The_Gateway_Should_Answer_Reads_And_Hold_Writes()
    {
        var data = Data;
        var gateway = new FixtureAzureClients(data).Gateway(null!, new FixtureCredential());
        const string sub = FixtureSamples.SubscriptionA;
        (await gateway.GetResourceGroupAsync(sub, "rg-mock-00"))!.CreatedByTool.Should().BeTrue();
        (await gateway.GetResourceGroupAsync(sub, "rg-none")).Should().BeNull();
        var withoutGroups = new FixtureGateway(data with { ResourceGroups = null });
        (await withoutGroups.GetResourceGroupAsync(sub, "rg-mock-00")).Should().Be(new ResourceGroupInfo("rg-mock-00", FixtureSamples.Location));
        (await gateway.GetEnvironmentAsync(sub, "rg-mock-00", "app00-env"))!.ProvisioningState.Should().Be("Succeeded");
        (await gateway.GetEnvironmentAsync(sub, "rg-mock-00", "other-env")).Should().BeNull();
        (await gateway.ListContainerAppsAsync(sub, "rg-mock-00")).Should().HaveCount(3).And.OnlyContain(a => a.EnvironmentId!.EndsWith("/app00-env"));
        (await gateway.ListContainerAppsAsync(sub, "rg-none")).Should().BeEmpty();
        (await gateway.ListContainerAppDetailsAsync(sub, "rg-mock-00")).Single(a => a.Name == "app00-tt").Fqdn.Should().StartWith("app00-tt.");
        (await gateway.GetContainerAppSecretsAsync(sub, "rg-mock-00", "app00-tt"))![ContainerAppSpecBuilder.AppPasswordSecretName].Should().Be("App-Pass-00");
        (await gateway.GetContainerAppSecretsAsync(sub, "rg-mock-00", "app00-db")).Should().BeNull();
        (await new FixtureGateway(data with { Secrets = null }).GetContainerAppSecretsAsync(sub, "rg-mock-00", "app00-tt")).Should().BeNull();
        (await gateway.ListResourcesAsync(sub, "rg-mock-00")).Should().HaveCount(4);
        (await gateway.GetPermissionsAsync(sub, "rg-mock-00")).Single().Actions.Should().Equal("*");
        (await gateway.GetResourcePermissionsAsync(sub, "rg-mock-00", "Microsoft.App", "containerApps", "app00-tt")).Single().Actions.Should().Equal("*");

        // ストレージ（DB のデータの永続化）
        var storage = new ManagedResourceRecord("/s", "stmock", ManagedResourceRecord.StorageAccountType, sub, "rg-mock-00", FixtureSamples.Location,
            new Dictionary<string, string> { [ManagedTags.ManagedByKey] = ManagedTags.ManagedByValue });
        var app = data.Resources.First(r => r.Name == "app00-db");
        var persisted = new FixtureGateway(data with
        {
            Resources = [.. data.Resources.Where(r => r != app), app with { App = app.App! with { StorageNames = [DbPersistence.StorageName] } }, storage],
        });
        (await gateway.GetStorageAccountAsync(sub, "rg-mock-00", "stmock")).Should().BeNull();
        (await persisted.GetStorageAccountAsync(sub, "rg-mock-00", "stmock"))!.Name.Should().Be("stmock");
        (await gateway.IsStorageAccountNameAvailableAsync(sub, "stmock")).Should().BeTrue();
        (await persisted.IsStorageAccountNameAvailableAsync(sub, "stmock")).Should().BeFalse();
        (await gateway.FileShareExistsAsync(sub, "rg-mock-00", "stmock", "share")).Should().BeFalse();
        (await persisted.FileShareExistsAsync(sub, "rg-mock-00", "stmock", "share")).Should().BeTrue();
        (await gateway.EnvironmentStorageExistsAsync(sub, "rg-mock-00", "app00-env", DbPersistence.StorageName)).Should().BeFalse();
        (await persisted.EnvironmentStorageExistsAsync(sub, "rg-mock-00", "app00-env", DbPersistence.StorageName)).Should().BeTrue();

        // 書き込みは、決めた時間を待ってから終わる（削除は「削除した」、Environment・Container App の作成は例外）。取り消せる
        var slow = new FixtureGateway(data with { WriteDelaySeconds = 0.05 });
        var tags = new Dictionary<string, string>();
        await slow.EnsureResourceGroupAsync(sub, "rg", "japaneast");
        await slow.EnsureDbStorageAsync(sub, "rg", "app", "japaneast", null!);
        await slow.UpdateContainerAppTagsAsync(sub, "rg", "app", tags);
        await slow.UpdateStorageAccountTagsAsync(sub, "rg", "st", tags);
        await slow.DeleteContainerAppTagsAsync(sub, "rg", "app", tags);
        await slow.DeleteStorageAccountTagsAsync(sub, "rg", "st", tags);
        (await slow.DeleteContainerAppAsync(sub, "rg", "app")).Should().BeTrue();
        (await slow.DeleteEnvironmentAsync(sub, "rg", "env")).Should().BeTrue();
        (await slow.DeleteStorageAccountAsync(sub, "rg", "st")).Should().BeTrue();
        (await slow.DeleteResourceGroupAsync(sub, "rg")).Should().BeTrue();
        await FluentActions.Awaiting(() => slow.EnsureContainerAppEnvAsync(sub, "rg", "app", "japaneast", null)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => slow.CreateOrUpdateContainerAppAsync(sub, "rg", "app", "japaneast",
            new DeployOptions { Subscription = sub, ResourceGroup = "rg", DbPassword = "p", TrackerPassword = "p" }, null!)).Should().ThrowAsync<InvalidOperationException>();
        using var cts = new CancellationTokenSource();
        var held = gateway.DeleteContainerAppAsync(sub, "rg", "app", cts.Token);
        await Task.Delay(10);
        held.IsCompleted.Should().BeFalse("既定は 2 秒待つ");
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => held).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_Directories_Should_Return_The_Data()
    {
        var data = Data;
        var clients = new FixtureAzureClients(data);
        var credential = new FixtureCredential();
        (await clients.Tenants(credential).ListAsync()).Single().TenantId.Should().Be(FixtureSamples.TenantId);
        (await clients.Subscriptions(credential).ListAsync()).Should().HaveCount(2);
        (await clients.Deployments(credential).ListAsync([FixtureSamples.SubscriptionB])).Should().HaveCount(48).And.OnlyContain(r => r.SubscriptionId == FixtureSamples.SubscriptionB);
    }

    private static async Task WaitAsync(Func<bool> condition, string description)
    {
        for (var i = 0; i < 400 && !condition(); i++)
            await Task.Delay(10);
        condition().Should().BeTrue(description);
    }
}
