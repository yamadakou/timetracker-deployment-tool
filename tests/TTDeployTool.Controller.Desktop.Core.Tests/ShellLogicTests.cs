using FluentAssertions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Mvvm;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;
using static TTDeployTool.Controller.Desktop.Core.Tests.DeploymentListViewModelTests;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>画面の側から Core に移した判断（表の選択の受け渡し・見出しの並べ替え・起動と通知の振り分け・StartupTask の状態）。</summary>
public class ShellLogicTests
{
    private readonly MovableTime _time = new();

    [Fact]
    public void The_Table_Selection_Should_Follow_The_View_And_Be_Restored_Without_Echo()
    {
        IReadOnlyList<string> selected = [];
        var changes = new List<string[]>();
        var sync = new SelectionSync<string>(() => selected, items =>
        {
            selected = items.ToList();
            changes.Add([.. selected]);
        });
        var view = new List<object>();

        // 表を渡す前は何もしない
        sync.ViewChanged();
        sync.ViewModelChanged();
        changes.Should().BeEmpty();

        sync.Attach(view);
        view.AddRange(["a", "b", 1]);
        sync.ViewChanged();
        changes.Should().ContainSingle().Which.Should().Equal("a", "b");

        // 同じなら表を変えない
        sync.ViewModelChanged();
        view.Should().Equal("a", "b", 1);

        // ViewModel が選び直したら表に戻す。戻している間に表が出す選択の変更（ListView の SelectionChanged）は、ViewModel に返さない
        selected = ["b"];
        var table = new System.Collections.ObjectModel.ObservableCollection<object>(view);
        var echo = new SelectionSync<string>(() => selected, _ => throw new InvalidOperationException("戻している間に返した"));
        table.CollectionChanged += (_, _) => echo.ViewChanged();
        echo.Attach(table);
        echo.ViewModelChanged();
        table.Should().Equal("b");
    }

    [Fact]
    public async Task The_Tables_Should_Keep_Their_Selection_In_Step_With_The_View()
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "a"), .. Deployment("sub", "rg", "b")];
        await h.ReadyAsync();
        var list = h.Main.Deployments;
        var view = new List<object>();
        list.TableSelection.Attach(view);

        view.Add(list.Rows[1]);
        list.TableSelection.ViewChanged();
        list.Current.Should().BeSameAs(list.Rows[1]);

        // 読み直すと行は作り直すが、同じデプロイは選んだまま。表にも新しい行を戻す
        await list.LoadCommand.ExecuteAsync();
        view.Should().Equal(list.Rows[1]);
        await list.ClearSelectionCommand.ExecuteAsync();
        view.Should().BeEmpty();

        var jobs = h.Main.JobList;
        var jobView = new List<object>();
        jobs.TableSelection.Attach(jobView);
        var job = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "x"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        jobView.Add(job);
        jobs.TableSelection.ViewChanged();
        jobs.Current.Should().Be(job);
        await jobs.ClearSelectionCommand.ExecuteAsync();
        jobView.Should().BeEmpty();
    }

    [Fact]
    public async Task Header_Tags_Should_Name_The_Sort_Columns()
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "b"), .. Deployment("sub", "rg", "a")];
        await h.ReadyAsync();

        h.Main.Deployments.SortBy("AppName");
        h.Main.Deployments.Rows.Select(r => r.AppName).Should().Equal("a", "b");
        h.Main.JobList.SortBy("Time");
        h.Main.JobList.TimeHeader.Should().Be("予定日時 ▲");

        var unknown = () => h.Main.Deployments.SortBy("Nothing");
        unknown.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Starting_Should_Choose_The_Windows_Sign_In_Path()
    {
        var normal = new Harness(time: _time);
        await normal.Main.StartAsync(atStartup: false);
        normal.Notifications.Shown.Should().BeEmpty();

        var startup = new Harness(time: _time);
        await startup.Main.StartAsync(atStartup: true);
        startup.Notifications.Shown.Should().ContainSingle().Which.Page.Should().Be(Pages.Target);
    }

    [Fact]
    public async Task A_Notification_Should_Open_Its_Page_Or_Job()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var job = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "demo"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);

        await h.Main.OpenFromNotificationAsync(new Dictionary<string, string> { [AppNotice.PageArgument] = Pages.Deployments });
        h.Navigations.Should().EndWith(Pages.Deployments);

        await h.Main.OpenFromNotificationAsync(new Dictionary<string, string> { [AppNotice.JobArgument] = job.Id });
        (h.Navigations.Last(), h.Main.StatusJob).Should().Be((Pages.Status, job));

        // 消えたジョブ・知らない画面・引数なしは、ジョブの一覧
        foreach (var arguments in new[]
                 {
                     new Dictionary<string, string> { [AppNotice.JobArgument] = "job-999" },
                     new Dictionary<string, string> { [AppNotice.PageArgument] = "unknown" },
                     new Dictionary<string, string>(),
                 })
        {
            await h.Main.OpenFromNotificationAsync(arguments);
            h.Navigations.Should().EndWith(Pages.Jobs);
        }
    }

    [Fact]
    public async Task A_Notification_Without_A_Window_Listening_Should_Do_Nothing()
    {
        var h = new Harness(time: _time);
        h.Main.NavigationRequested -= h.Navigations.Add;

        await h.Main.OpenFromNotificationAsync(new Dictionary<string, string> { [AppNotice.PageArgument] = Pages.Deployments });
        await h.Main.OpenFromNotificationAsync(new Dictionary<string, string>());

        h.Navigations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Enabled", StartupTaskStatus.Enabled)]
    [InlineData("EnabledByPolicy", StartupTaskStatus.Enabled)]
    [InlineData("DisabledByUser", StartupTaskStatus.DisabledByUser)]
    [InlineData("DisabledByPolicy", StartupTaskStatus.DisabledByPolicy)]
    [InlineData("Disabled", StartupTaskStatus.Disabled)]
    [InlineData("SomethingNew", StartupTaskStatus.Disabled)]
    public void Windows_Startup_Task_States_Should_Be_Mapped(string name, StartupTaskStatus expected) =>
        StartupTaskStates.FromWindowsState(name).Should().Be(expected);
}
