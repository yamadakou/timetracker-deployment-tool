using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using TTDeployTool.Controller.Desktop.Blazor.Shell;
using TTDeployTool.Controller.Desktop.Core.ViewModels;

namespace TTDeployTool.Controller.Desktop.Blazor.Tests;

/// <summary>画面の根（左のメニュー・上の帯・画面の切り替え・確かめるダイアログ。desktop-blazor-hybrid.md の 16）と、ホストと共有する状態。</summary>
public sealed class ShellTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private IRenderedComponent<DesktopShell> Render() => _app.Context.Render<DesktopShell>();

    private static IElement Nav(IRenderedComponent<DesktopShell> shell, string id) => shell.Find($"[data-id='Nav.{id}']");

    [Fact]
    public void Before_Sign_In_Only_The_Pages_Without_Azure_Should_Open()
    {
        var shell = Render();
        shell.Find("[data-id='Panel.Target']");
        shell.Find("[data-id='SignIn.Account']").TextContent.Should().StartWith("サインインしていません");
        Nav(shell, "Target").GetAttribute("aria-current").Should().Be("page");
        Nav(shell, "Deployments").HasAttribute("disabled").Should().BeTrue();
        Nav(shell, "Wizard").HasAttribute("disabled").Should().BeTrue();
        Nav(shell, "Jobs").HasAttribute("disabled").Should().BeFalse();
        shell.FindAll("[data-id='Jobs.Banner']").Should().BeEmpty();
        shell.FindAll("[data-id='UpdateBar']").Should().BeEmpty();

        foreach (var (nav, page) in new[] { ("DryRun", Pages.DryRun), ("AppSettings", Pages.Settings), ("Jobs", Pages.Jobs), ("Target", Pages.Target) })
        {
            Nav(shell, nav).Click();
            shell.Find($"[data-id='Panel.{nav}']");
            shell.FindAll("section.tt-page").Should().ContainSingle();
            Nav(shell, nav).GetAttribute("aria-current").Should().Be("page");
            shell.Instance.Page.Should().Be(page);
            if (nav == "Jobs")
                shell.Find("[data-id='Jobs.SignInNote']");
        }
    }

    [Fact]
    public async Task Sign_In_Should_Open_The_Lists_And_Row_Operations_Should_Open_Their_Pages()
    {
        var shell = Render();
        shell.Find("[data-id='SignIn.SignIn']").Click();
        shell.WaitForAssertion(() => shell.Find("[data-id='SignIn.Account']").TextContent.Should().Be("mock-user@example.com"));
        Nav(shell, "Deployments").HasAttribute("disabled").Should().BeFalse();

        Nav(shell, "Deployments").Click();
        shell.WaitForAssertion(() => shell.FindAll("tbody tr").Should().HaveCount(20));

        // 1 行: 削除の画面。戻る
        shell.FindAll("tbody tr")[0].Click();
        shell.Find("[data-id='Deployments.Delete']").Click();
        shell.WaitForAssertion(() => shell.Find("[data-id='Panel.Delete']"));
        shell.Find("[data-id='Delete.BackToList']").Click();
        shell.WaitForAssertion(() => shell.Find("[data-id='Panel.Deployments']"));

        // 2 行: まとめての削除の画面。戻る
        shell.FindAll("tbody tr")[1].Click();
        shell.WaitForAssertion(() => _app.ViewModel.Deployments.Selected.Should().HaveCount(2));
        shell.Find("[data-id='Deployments.Delete']").Click();
        shell.WaitForAssertion(() => shell.Find("[data-id='Panel.BatchDelete']"));
        shell.Find("[data-id='BatchDelete.BackToList']").Click();
        shell.WaitForAssertion(() => shell.Find("[data-id='Panel.Deployments']"));

        // 状態を確認: 実行状況の画面。戻る
        shell.Find("[data-id='Deployments.ClearSelection']").Click();
        shell.FindAll("tbody tr")[0].Click();
        shell.Find("[data-id='Row.CheckStatus']").Click();
        shell.WaitForAssertion(() => shell.Find("[data-id='Panel.Status']"));
        shell.Find("[data-id='Status.BackToList']").Click();
        shell.WaitForAssertion(() => shell.Find("[data-id='Panel.Deployments']"));

        Nav(shell, "Wizard").Click();
        shell.Find("[data-id='Panel.Wizard']");

        // サインアウトすると、サインインの画面に戻る
        Nav(shell, "Target").Click();
        shell.Find("[data-id='SignIn.SignOut']").Click();
        shell.WaitForAssertion(() => Nav(shell, "Deployments").HasAttribute("disabled").Should().BeTrue());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Banners_Should_Show_Updates_And_Unfinished_Jobs()
    {
        var shell = Render();
        await _app.SignInAsync();

        _app.Session.UpdateAvailable = true;
        shell.WaitForAssertion(() => shell.Find("[data-id='UpdateBar']"));
        shell.Find("[data-id='UpdateBar.Close']").Click();
        _app.Session.UpdateAvailable.Should().BeFalse();
        shell.FindAll("[data-id='UpdateBar']").Should().BeEmpty();

        // 予定（1 時間後）の削除で、上の帯に終わっていないジョブを出す
        var list = _app.ViewModel.Deployments;
        list.SetSelection([list.Rows[0]]);
        await list.DeleteSelectedCommand.ExecuteAsync();
        await _app.ViewModel.Deletion.PlanCommand.ExecuteAsync();
        _app.ViewModel.Deletion.ConfirmAppName = list.Rows[0].AppName;
        _app.ViewModel.Deletion.Schedule.Date = TestApp.Now.AddHours(1).ToLocalTime();
        await _app.ViewModel.Deletion.ScheduleCommand.ExecuteAsync();
        shell.WaitForAssertion(() => shell.Find("[data-id='Jobs.Banner']").TextContent.Should().Contain("予定"));
        shell.Find("[data-id='Jobs.BannerShow']").Click();
        shell.Find("[data-id='Panel.Jobs']");
    }

    [Fact]
    public async Task Confirm_Dialog_Should_Answer_With_The_Buttons_And_Escape()
    {
        var shell = Render();
        var answer = _app.Dialogs.ConfirmAsync("記録から削除", "1 件を消します。", "記録から削除");
        shell.WaitForAssertion(() => shell.Find("[data-id='Dialog.Title']").TextContent.Should().Be("記録から削除"));
        shell.Find("[data-id='Dialog.Message']").TextContent.Should().Be("1 件を消します。");
        shell.Find("[data-id='Dialog.Cancel']").HasAttribute("autofocus").Should().BeTrue();
        shell.Find("[data-id='Dialog']").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        answer.IsCompleted.Should().BeFalse("Esc のほかのキーでは閉じない");
        shell.Find("[data-id='Dialog.Confirm']").Click();
        (await answer).Should().BeTrue();
        shell.FindAll("[data-id='Dialog']").Should().BeEmpty();

        answer = _app.Dialogs.ConfirmAsync("t", "m", "c");
        shell.WaitForAssertion(() => shell.Find("[data-id='Dialog']"));
        shell.Find("[data-id='Dialog.Cancel']").Click();
        (await answer).Should().BeFalse();

        answer = _app.Dialogs.ConfirmAsync("t", "m", "c");
        shell.WaitForAssertion(() => shell.Find("[data-id='Dialog']"));
        shell.Find("[data-id='Dialog']").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        (await answer).Should().BeFalse();
    }

    [Fact]
    public async Task Dialog_Service_Should_Cancel_An_Older_Question_And_Pick_A_Folder()
    {
        var first = _app.Dialogs.ConfirmAsync("1", "m", "c");
        var second = _app.Dialogs.ConfirmAsync("2", "m", "c");
        (await first).Should().BeFalse("前のダイアログは「キャンセル」にする");
        _app.Dialogs.Pending!.Title.Should().Be("2");
        _app.Dialogs.Answer(true);
        (await second).Should().BeTrue();
        _app.Dialogs.Answer(true);
        _app.Dialogs.Pending.Should().BeNull();

        (await _app.Dialogs.PickFolderAsync()).Should().BeNull();
        _app.Folder = "/tmp/x";
        (await _app.Dialogs.PickFolderAsync()).Should().Be("/tmp/x");
    }

    [Fact]
    public async Task Session_Should_Start_Once_And_Register_The_Services()
    {
        var runs = 0;
        var session = new DesktopSession(_app.ViewModel, _app.Dialogs, null, _ =>
        {
            runs++;
            return Task.CompletedTask;
        });
        await session.StartAsync();
        await session.StartAsync();
        runs.Should().Be(1);
        session.Clipboard.Should().BeNull();
        session.Dialogs.Should().BeSameAs(_app.Dialogs);

        // 既定の始めの処理（通常の起動）
        await _app.Session.StartAsync();
        _app.Session.ViewModel.Should().BeSameAs(_app.ViewModel);

        var services = new ServiceCollection();
        services.AddTTDeployToolDesktop(session);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<DesktopSession>().Should().BeSameAs(session);
        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public async Task Shell_Should_Say_Nothing_Until_The_Start_Finishes_And_Stop_Following_After_Dispose()
    {
        using var other = new BunitContext();
        other.JSInterop.Mode = JSRuntimeMode.Loose;
        other.JSInterop.SetupModule(Tables.JsModule.Path).Mode = JSRuntimeMode.Loose;
        var start = new TaskCompletionSource();
        other.Services.AddTTDeployToolDesktop(new DesktopSession(_app.ViewModel, _app.Dialogs, null, _ => start.Task));
        var shell = other.Render<DesktopShell>();
        shell.Find("[data-id='Panel.Target']");
        start.SetResult();
        var instance = shell.Instance;
        await other.DisposeComponentsAsync();
        // 閉じた後に ViewModel が画面を開くよう求めても、何もしない
        var job = _app.ViewModel.JobList.Items[0];
        _ = _app.ViewModel.OpenJobAsync(job);
        instance.Page.Should().Be(Pages.Target);
    }
}
