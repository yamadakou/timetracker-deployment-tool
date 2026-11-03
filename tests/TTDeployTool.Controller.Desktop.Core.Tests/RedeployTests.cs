using System.Text.Json;
using FluentAssertions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// デプロイのジョブの「この設定で新しくデプロイ」（オーナーの依頼、2026-11-03。オーナーの判断で案 A・案 1・案 a）。デプロイのジョブに入力の写し（パスワードは含めない）を持たせて
/// 記録にも残し、ジョブの一覧・実行状況の画面・デプロイの一覧の詳細から、「新しくデプロイ」に読み込んで ① から開く。終わったデプロイのジョブ（完了・失敗・停止・取り消し）で使える。
/// </summary>
public sealed class RedeployTests
{
    private static DeployOptions Options(string tag = "", string? expiresOn = null, string dbType = "postgresql") => DeployOptionsResolver.Resolve(new DeployInput
    {
        Subscription = "sub", ResourceGroup = "rg-x", AppName = "app-x", Location = "westus", DbType = dbType, DbName = "db1", TimetrackerTag = tag,
        DbPassword = "DbPassw0rd!", TrackerPassword = "TrackerPass!", TimetrackerCpu = 1.25, TimetrackerMemoryGi = 2.5, DbCpu = 0.5, DbMemoryGi = 1,
        RedisCpu = 0.25, RedisMemoryGi = 0.5, PersistDb = true, ExpiresOn = expiresOn, Group = "チーム A",
    });

    [Fact]
    public void The_Settings_Should_Be_Made_From_The_Options_Without_Passwords()
    {
        var t = DeploymentTemplate.From(Options(expiresOn: "2026-12-01"));
        t.Should().Be(new DeploymentTemplate("", "sub", "rg-x", "app-x", "westus", "postgresql", "db1", "", "1.25", "2.5", "0.5", "1.0", "0.25", "0.5", true,
            ExpiryMode.Date, "2026-12-01", "チーム A"));
        JsonSerializer.Serialize(t).Should().NotContain("DbPassw0rd!").And.NotContain("TrackerPass!");
    }

    [Theory]
    [InlineData(null, ExpiryMode.Default, null)]
    [InlineData("none", ExpiryMode.None, null)]
    [InlineData("NONE", ExpiryMode.None, null)]
    [InlineData("2026-11-02", ExpiryMode.Date, "2026-11-02")]
    public void The_Expiry_Should_Keep_The_Way_It_Was_Given(string? expiresOn, ExpiryMode mode, string? date)
    {
        var t = DeploymentTemplate.From(Options(expiresOn: expiresOn));
        (t.Expiry, t.ExpiryDate).Should().Be((mode, date));
    }

    [Fact]
    public void Only_A_Tag_Other_Than_The_Default_Of_The_Db_Type_Should_Be_Kept()
    {
        // 既定のタグは空（既定）にする（DB の種類を変えたときに、その種類の既定になるように）
        DeploymentTemplate.From(Options(dbType: "sqlserver")).TtTag.Should().BeEmpty();
        DeploymentTemplate.From(Options(tag: "9.9.9")).TtTag.Should().Be("9.9.9");
    }

    [Fact]
    public async Task A_Finished_Deployment_Should_Load_Its_Settings_Into_The_Wizard()
    {
        var h = await new Harness().ReadyAsync();
        h.Main.Settings.TtCpu = "1.25";
        h.Main.Settings.Group = "チーム B";
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        var job = h.Main.Jobs.Single();
        job.Settings.Should().NotBeNull();
        (job.Settings!.ResourceGroup, job.Settings.AppName, job.Settings.TtCpu, job.Settings.Group).Should().Be(("rg", "demo", "1.25", "チーム B"));
        (job.CanRedeploy, job.RedeployNote).Should().Be((true, (string?)null));

        // 入力を変えてから読み込むと、ジョブの設定に戻り、パスワードはそのまま。「新しくデプロイ」の ① を開く
        h.Main.Target.ResourceGroup = "other";
        h.Main.Settings.TtCpu = "0.5";
        h.Main.Settings.Group = "";
        h.Navigations.Clear();
        await job.RedeployCommand.ExecuteAsync();
        (h.Main.Target.ResourceGroup, h.Main.Target.AppName, h.Main.Settings.TtCpu, h.Main.Settings.Group).Should().Be(("rg", "demo", "1.25", "チーム B"));
        (h.Main.Settings.DbPassword, h.Main.Settings.TrackerPassword).Should().Be(("DbPassw0rd!", "TrackerPass!"));
        h.Navigations.Should().Equal(Pages.Wizard);
        (h.Main.Wizard.Step, h.Main.Wizard.IsCompleted).Should().Be((WizardStep.Target, false));
        h.Main.Settings.Message.Should().Be("ジョブの設定（rg / demo）を入力しました。パスワードは記録に残していないため、③ で入れてください（入力済みなら、そのまま使います）。");
    }

    [Fact]
    public async Task The_Settings_Should_Be_Kept_In_The_Record_And_Used_After_A_Restart()
    {
        var h = await new Harness().ReadyAsync();
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        var record = h.History.Records.Should().ContainSingle().Subject;
        record.Settings.Should().Be(h.Main.Jobs.Single().Settings);
        JsonSerializer.Serialize(record).Should().NotContain("DbPassw0rd!").And.NotContain("TrackerPass!");

        var restarted = await new Harness(history: h.History).ReadyAsync();
        var restored = restarted.Main.Jobs.Single();
        (restored.IsRecord, restored.CanRedeploy).Should().Be((true, true));
        restarted.Main.Target.ResourceGroup = "other";
        await restored.RedeployCommand.ExecuteAsync();
        restarted.Main.Target.ResourceGroup.Should().Be("rg");
    }

    [Fact]
    public void Records_Without_Settings_Should_Tell_Why_They_Cannot_Be_Loaded()
    {
        // この機能より前の記録（設定が無い）と削除は読み込めない。デプロイの記録には理由を出す
        var history = new InMemoryJobHistory
        {
            Records =
            [
                new(JobKind.Deploy, "sub", "rg", "old", false, null, DateTimeOffset.UtcNow, DesktopJobState.Failed, "x", null, null, null, null, null, []),
                new(JobKind.Delete, "sub", "rg", "gone", false, null, DateTimeOffset.UtcNow, DesktopJobState.Succeeded, null, null, null, null, null, null, []),
            ],
        };
        var h = new Harness(history: history);
        var old = h.Main.Jobs.Single(j => j.IsDeployment);
        (old.CanRedeploy, old.RedeployCommand.CanExecute(null)).Should().Be((false, false));
        old.RedeployNote.Should().StartWith("このジョブの記録には設定が残っていないため");
        var deletion = h.Main.Jobs.Single(j => !j.IsDeployment);
        (deletion.CanRedeploy, deletion.RedeployNote).Should().Be((false, (string?)null));
    }

    [Fact]
    public async Task Jobs_That_Have_Not_Finished_Should_Not_Be_Loaded_Until_They_Finish()
    {
        // 案 a: 終わったデプロイのジョブなら、取り消しでも使える
        var h = await new Harness().ReadyAsync();
        var settings = DeploymentTemplate.From(Options());
        var job = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg-x", "app-x"), DateTimeOffset.UtcNow.AddHours(1),
            () => new TaskCompletionSource<Job>().Task, settings: settings);
        (job.CanRedeploy, job.RedeployNote, job.RedeployCommand.CanExecute(null)).Should().Be((false, (string?)null, false));
        var changes = new List<string?>();
        job.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await job.CancelCommand.ExecuteAsync();
        (job.State, job.CanRedeploy, job.RedeployCommand.CanExecute(null)).Should().Be((DesktopJobState.Canceled, true, true));
        changes.Should().Contain([nameof(JobViewModel.CanRedeploy), nameof(JobViewModel.RedeployNote)]);
    }

    [Fact]
    public void Jobs_Outside_The_App_Should_Not_Be_Loaded()
    {
        // 画面の無いジョブ（JobQueue の無い表示）は、読み込む先が無い
        var queue = new JobQueue(TimeProvider.System, TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(15), new InMemoryAppPreferences(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var job = queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "app"), DateTimeOffset.UtcNow.AddHours(1), () => new TaskCompletionSource<Job>().Task,
            settings: DeploymentTemplate.From(Options()));
        queue.Cancel(job);
        job.CanRedeploy.Should().BeFalse("読み込む先（Redeploy）が無い");
        queue.Dispose();
    }

    [Fact]
    public async Task The_Deployment_List_Should_Load_The_Settings_Of_The_Last_Deployment_Of_The_Row()
    {
        var h = new Harness();
        h.Azure.RecordsByTenant["t1"] = [.. DeploymentListViewModelTests.Deployment("sub", "rg", "demo")];
        h.Azure.Deployed();
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        var row = h.Main.Deployments.Rows.Single();
        row.RedeployCommand.CanExecute(null).Should().BeFalse("設定のあるデプロイのジョブが無い");

        // 2 回デプロイして、後のものの設定を読み込む。ほかの対象・削除のジョブは見ない
        foreach (var cpu in new[] { "0.75", "1.75" })
        {
            h.Main.Settings.TtCpu = cpu;
            await h.Main.Deployment.PlanCommand.ExecuteAsync();
            h.Main.Deployment.DataLossAcknowledged = true;
            await h.Main.Deployment.StartCommand.ExecuteAsync();
            await h.Main.Queue.WhenIdleAsync();
            h.Main.Deployment.Error.Should().BeNull();
        }
        h.Main.Jobs.Count(j => j.IsDeployment && j.State == DesktopJobState.Succeeded).Should().Be(2);
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "other"), null, () => new TaskCompletionSource<Job>().Task,
            settings: DeploymentTemplate.From(Options()) with { TtCpu = "3.0" });
        row = h.Main.Deployments.Rows.Single();
        row.RedeployCommand.CanExecute(null).Should().BeTrue();
        h.Main.Settings.TtCpu = "0.5";
        h.Navigations.Clear();
        await row.RedeployCommand.ExecuteAsync();
        (h.Main.Settings.TtCpu, h.Navigations.Last()).Should().Be(("1.75", Pages.Wizard));

        // 同じ対象の終わっていないジョブがある間は押せない
        var raised = 0;
        row.RedeployCommand.CanExecuteChanged += (_, _) => raised++;
        var pending = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "demo"), DateTimeOffset.UtcNow.AddHours(1), () => new TaskCompletionSource<Job>().Task);
        row.RedeployCommand.CanExecute(null).Should().BeFalse();
        raised.Should().BePositive("ジョブが変わったら押せるかを出し直す");
        await pending.CancelCommand.ExecuteAsync();
        row.RedeployCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Templates_Should_Still_Be_Applied_With_Their_Own_Message()
    {
        var h = await new Harness().ReadyAsync();
        var settings = h.Main.Settings;
        settings.TemplateName = "既定";
        await settings.SaveTemplateCommand.ExecuteAsync();
        settings.TtCpu = "0.9";
        await settings.ApplyTemplateCommand.ExecuteAsync();
        (settings.TtCpu, settings.Message).Should().Be(("1.5", "テンプレート「既定」を入力しました（パスワードはそのままです）。"));
    }
}
