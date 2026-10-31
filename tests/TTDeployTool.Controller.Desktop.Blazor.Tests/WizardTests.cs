using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using TTDeployTool.Controller.Desktop.Blazor.Screens;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Controller.Desktop.Fixtures;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>新しくデプロイ（① 対象〜⑤ 確認と実行、完了の表示。WinUI の画面の WizardPanel と同じ入力が Core に届くこと）。</summary>
public sealed class WizardTests : IAsyncLifetime
{
    private readonly TestApp _app = new(secrets: true);
    private IRenderedComponent<WizardPage> _page = default!;

    private DeployWizardViewModel Wizard => _app.ViewModel.Wizard;

    private SettingsViewModel Settings => _app.ViewModel.Settings;

    public async ValueTask InitializeAsync()
    {
        await _app.SignInAsync();
        _page = _app.Context.Render<WizardPage>(p => p.Add(x => x.Model, _app.ViewModel));
    }

    public ValueTask DisposeAsync()
    {
        _app.Dispose();
        return ValueTask.CompletedTask;
    }

    private IElement Id(string id) => _page.Find($"[data-id='{id}']");

    private void Type(string id, string text) => Id(id).Input(text);

    private void Click(string id) => Id(id).Click();

    private void Next(WizardStep step)
    {
        Click("Wizard.Next");
        _page.WaitForAssertion(() => Wizard.Step.Should().Be(step));
    }

    // ①〜④ を入れて ⑤ を開く（対象は、既にある app00。DB は永続化していないため、更新で DB のデータが失われる）
    private void FillToConfirm(string appName = "app00", string persist = "0")
    {
        var subscriptions = _app.ViewModel.Target.Subscriptions.ToList();
        Id("Target.Subscription").Change($"{subscriptions.FindIndex(s => s.SubscriptionId == FixtureSamples.Subscription(0))}");
        Type("Target.ResourceGroup", FixtureSamples.ResourceGroup(0));
        Type("Target.AppName", appName);
        Type("Target.Location", "japaneast");
        Next(WizardStep.Db);
        Id("Settings.DbType").Change("0");
        Id("Settings.DbType").Change("");
        Type("Settings.DbName", "timetracker");
        Type("Settings.TtTag", "");
        Id("Settings.PersistDb").Change(persist);
        Next(WizardStep.Passwords);
        Type("Settings.DbPassword", "Wizard-Db-Pass1!");
        Type("Settings.TrackerPassword", "Wizard-Tt-Pass1!");
        Next(WizardStep.Spec);
        foreach (var (id, value) in new[] { ("Settings.TtCpu", "1"), ("Settings.TtMemory", "2"), ("Settings.DbCpu", "0.5"), ("Settings.DbMemory", "1"), ("Settings.RedisCpu", "0.25"), ("Settings.RedisMemory", "0.5") })
            Type(id, value);
        Id("Settings.Expiry").Change("1");
        Settings.IsExpiryDate.Should().BeTrue();
        Id("Settings.ExpiryDate").Change(TestApp.Now.AddDays(10).ToString("yyyy-MM-dd"));
        Settings.ExpiryDate.Date.Should().Be(TestApp.Now.AddDays(10).Date);
        Id("Settings.Expiry").Change("2");
        _page.FindAll("[data-id='Settings.ExpiryDate']").Should().BeEmpty("期限なしなら日付を出さない");
        Type("Settings.Group", "研");
        _page.FindAll("#tt-group-suggestions option").Select(o => o.GetAttribute("value")).Should().Contain("研修");
        Type("Settings.Group", "研修");
        Id("Settings.GroupNotice").TextContent.Should().NotBeEmpty();
        Next(WizardStep.Confirm);
        _page.WaitForAssertion(() => _app.ViewModel.Deployment.HasPlan.Should().BeTrue());
    }

    [Fact]
    public void The_Steps_Should_Show_Errors_And_Go_Back_And_Forth()
    {
        Id("Wizard.Step").TextContent.Should().Be(Wizard.StepText).And.EndWith("（1 / 5）");
        Id("Settings.Template").QuerySelector("option")!.TextContent.Should().Be("（テンプレートなし）");
        Click("Settings.CopyMarkdown");
        _app.Clipboard.Texts.Should().ContainSingle();
        Id("Settings.IncludePasswords").Change(true);
        Settings.IncludePasswordsInCopy.Should().BeTrue();
        Type("Target.AppName", "");
        _page.WaitForAssertion(() => Id("Wizard.Errors").TextContent.Should().NotBeEmpty());
        Id("Wizard.Next").HasAttribute("disabled").Should().BeTrue();
        Id("Wizard.Blocked").TextContent.Should().NotBeEmpty();

        Id("Target.Subscription").Change("0");
        Type("Target.AppName", "newapp");
        Type("Target.ResourceGroup", "rg-new");
        Next(WizardStep.Db);
        Click("Wizard.Back");
        _page.WaitForAssertion(() => Wizard.Step.Should().Be(WizardStep.Target));
        Click("Target.Reload");
        Click("Settings.Reset");
        _page.WaitForAssertion(() => Id("Settings.Message").TextContent.Should().NotBeNull());
    }

    [Fact]
    public async Task A_Redeploy_Should_Need_The_Data_Loss_Check_And_Run_Now()
    {
        FillToConfirm(persist: "1");
        var deployment = _app.ViewModel.Deployment;
        deployment.RequiresDataLossAck.Should().BeTrue();
        _page.FindAll("[data-id='Deploy.Operations'] li.tt-critical").Should().NotBeEmpty();
        Id("Deploy.Expiry").TextContent.Should().NotBeEmpty();
        Id("Deploy.Group").TextContent.Should().Contain("研修");
        Id("Deploy.Start").HasAttribute("disabled").Should().BeTrue();
        Id("Deploy.DataLossAck").Change(true);
        deployment.DataLossAcknowledged.Should().BeTrue();
        Id("Deploy.KeepSchedule").Change(true);
        deployment.KeepScheduleAfterRestart.Should().BeTrue();
        Id("Deploy.ScheduleNote").TextContent.Should().NotBeNull();

        // テンプレートとして保存（計画は消えない）
        Type("Settings.TemplateName", "研修用");
        Click("Settings.SaveTemplate");
        _page.WaitForAssertion(() => Settings.Templates.Should().ContainSingle());
        deployment.HasPlan.Should().BeTrue();

        Click("Deploy.Start");
        _page.WaitForAssertion(() => Wizard.IsCompleted.Should().BeTrue());
        Id("Wizard.Step").TextContent.Should().Be("完了");
        Id("Wizard.CompletedText").TextContent.Should().EndWith("を実行しました。");
        await _app.ViewModel.Queue.WhenIdleAsync();
        _page.WaitForAssertion(() => Id("WizardJob.Status").TextContent.Should().Be(Wizard.RegisteredJob!.StatusText));
        _page.FindAll("[data-id='Wizard.Next']").Should().BeEmpty("完了の表示には、戻る・次へを出さない");
        Id("Wizard.ShowJob").TextContent.Should().Be("実行状況（ログ）を見る");
        Click("Wizard.ShowJob");
        _app.ViewModel.StatusJob.Should().BeSameAs(Wizard.RegisteredJob);

        Click("Wizard.Continue");
        _page.WaitForAssertion(() => Wizard.Step.Should().Be(WizardStep.Target));
        Wizard.IsCompleted.Should().BeFalse();

        // ① で、テンプレートから入力・削除（確かめる）
        Id("Settings.Template").Change("0");
        Settings.SelectedTemplate!.Name.Should().Be("研修用");
        Click("Settings.ApplyTemplate");
        Click("Settings.DeleteTemplate");
        _page.WaitForAssertion(() => _app.Dialogs.Pending.Should().NotBeNull());
        _app.Dialogs.Answer(true);
        _page.WaitForAssertion(() => Settings.Templates.Should().BeEmpty());
    }

    [Fact]
    public void A_New_Deployment_Should_Be_Scheduled_And_Start_Over_From_The_Defaults()
    {
        FillToConfirm("newapp");
        var deployment = _app.ViewModel.Deployment;
        deployment.RequiresDataLossAck.Should().BeFalse();
        _page.FindAll("[data-id='Deploy.DataLossAck']").Should().BeEmpty();
        Click("Deploy.Plan");
        _page.WaitForAssertion(() => deployment.HasPlan.Should().BeTrue());
        var at = TestApp.Now.AddHours(2).ToLocalTime();
        Id("Deploy.ScheduleDate").Change(at.ToString("yyyy-MM-dd"));
        Id("Deploy.ScheduleTime").Change(at.ToString("HH:mm"));
        Id("Deploy.ScheduleTime").Change("x");
        Id("Deploy.ScheduleDate").Change("x");
        deployment.Schedule.TimeOfDay.Should().Be(new TimeSpan(at.Hour, at.Minute, 0));
        Click("Deploy.Schedule");
        _page.WaitForAssertion(() => Wizard.IsCompleted.Should().BeTrue());
        Id("Wizard.CompletedText").TextContent.Should().Contain("予定に追加しました");
        Id("Wizard.ShowJob").TextContent.Should().Be("ジョブの一覧を見る");
        _page.FindAll("[data-id='WizardJob.Endpoint']").Should().BeEmpty();

        // ① に戻ると、同じ対象のジョブがある旨を出す
        Click("Wizard.Continue");
        _page.WaitForAssertion(() => Id("Wizard.ActiveJobNotice").TextContent.Should().Contain("newapp"));
        Next(WizardStep.Db);
        Next(WizardStep.Passwords);
        Next(WizardStep.Spec);
        Next(WizardStep.Confirm);
        _page.WaitForAssertion(() => Id("Deploy.Error").TextContent.Should().NotBeEmpty());

        // 完了の表示から、初期値で始める
        Click("Wizard.Back");
        _page.WaitForAssertion(() => Wizard.Step.Should().Be(WizardStep.Spec));
    }

    [Fact]
    public async Task New_Deployment_Should_Reset_The_Inputs()
    {
        FillToConfirm("newapp2");
        Click("Deploy.Start");
        _page.WaitForAssertion(() => Wizard.IsCompleted.Should().BeTrue());
        await _app.ViewModel.Queue.WhenIdleAsync();
        _page.WaitForAssertion(() => Id("WizardJob.Error").TextContent.Should().Contain("モック"));
        Click("Wizard.NewDeployment");
        _page.WaitForAssertion(() => Wizard.IsCompleted.Should().BeFalse());
        Settings.DbPassword.Should().BeEmpty();
    }
}
