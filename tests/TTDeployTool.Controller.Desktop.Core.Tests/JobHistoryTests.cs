using System.Text.Json;
using FluentAssertions;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>保存した回数を数える記録の保存先。</summary>
internal sealed class CountingJobHistory : IJobHistoryFile
{
    private IReadOnlyList<JobRecord> _records = [];

    public int Writes { get; private set; }

    public IReadOnlyList<JobRecord> Records
    {
        get => _records;
        set
        {
            _records = value;
            Writes++;
        }
    }

    /// <summary>保存の回数を数えずに、前回のアプリが保存したものを入れる。</summary>
    public CountingJobHistory With(IEnumerable<JobRecord> records)
    {
        _records = [.. records];
        return this;
    }
}

/// <summary>終わったジョブの記録（docs/design/desktop/desktop-jobs.md の 6.8、D29〜D32）。</summary>
public sealed class JobHistoryTests : IDisposable
{
    private readonly MovableTime _time = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ttdeploy-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private JobRecord Record(string app, DateTimeOffset finishedAt, DesktopJobState state = DesktopJobState.Succeeded) =>
        new(JobKind.Deploy, "sub", "rg", app, false, null, finishedAt, state, null, null, $"https://{app}.example.io", null, null, null, ["09:00:00 始めました"]);

    private static JobViewModel Pending(Harness h, string app, DateTimeOffset? at) =>
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", app), at, () => new TaskCompletionSource<Job>().Task);

    [Fact]
    public async Task Finished_Jobs_Should_Be_Kept_And_Shown_After_The_App_Restarts()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        var deployed = h.Main.Jobs.Single();
        deployed.FinishedAt.Should().Be(_time.Now);
        var at = $"{_time.Now.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        deployed.TimesText.Should().Be($"登録: {at}・実行開始: {at}・実行終了: {at}・処理時間: 0 分 0 秒", "このアプリで終わったものには、記録の旨を添えない");
        deployed.ToMarkdown().Should().Contain($"- {deployed.TimesText}");

        var record = h.History.Records.Should().ContainSingle().Subject;
        record.Should().BeEquivalentTo(new
        {
            Kind = JobKind.Deploy, Subscription = "sub", ResourceGroup = "rg", AppName = "demo", State = DesktopJobState.Succeeded, FinishedAt = _time.Now,
            Endpoint = deployed.Endpoint!.ToString(), deployed.PersistenceSummary, deployed.ExpirySummary, Error = (string?)null,
            RegisteredAt = (DateTimeOffset?)_time.Now, StartedAt = (DateTimeOffset?)_time.Now, ChangedAt = (DateTimeOffset?)null,
        });
        record.Logs.Should().Equal(deployed.Logs);
        JsonSerializer.Serialize(record).Should().NotContain("DbPassw0rd!").And.NotContain("TrackerPass!");

        // まだ始めていないものは残さず、取り消したら残す
        _time.Now = _time.Now.AddMinutes(5);
        var canceled = Pending(h, "other", _time.Now.AddHours(1));
        h.History.Records.Should().HaveCount(1);
        await canceled.CancelCommand.ExecuteAsync();
        h.History.Records.Select(r => (r.AppName, r.State)).Should().Equal(("demo", DesktopJobState.Succeeded), ("other", DesktopJobState.Canceled));

        var restarted = new Harness(time: _time, history: h.History);
        var jobs = restarted.Main.Jobs;
        jobs.Select(j => j.TargetText).Should().Equal("rg / other", "rg / demo");
        jobs.Should().OnlyContain(j => j.IsRecord && !j.IsActive);
        var restored = jobs[1];
        (restored.Title, restored.StatusText, restored.Endpoint, restored.HasEndpoint).Should().Be(("デプロイ（rg / demo）", "完了", deployed.Endpoint, true));
        (restored.PersistenceSummary, restored.ExpirySummary, restored.ResultText).Should().Be((deployed.PersistenceSummary, deployed.ExpirySummary, deployed.ResultText));
        restored.Logs.Should().Equal(deployed.Logs);
        restored.TimesText.Should().Be($"{deployed.TimesText}（前回までのアプリの記録）");
        (restored.RegisteredText, restored.StartedText, restored.DurationText).Should().Be((deployed.RegisteredText, deployed.StartedText, "0 分 0 秒"));
        restored.ToRecord().Should().BeEquivalentTo(record);
        jobs[0].TimeText.Should().MatchRegex(@"^\d\d/\d\d \d\d:\d\d$", "予定の時刻も残す");
        foreach (var command in new[] { restored.CancelCommand, restored.PauseCommand, restored.ResumeCommand, restored.RescheduleCommand, restored.StopCommand })
            command.CanExecute(null).Should().BeFalse();
        await restored.CopyCommand.ExecuteAsync();
        restarted.Clipboard.Copied.Should().ContainSingle().Which.Should().Contain("（前回までのアプリの記録）");
        await restarted.Main.Queue.WhenIdleAsync();
        restarted.Main.Queue.Summary.Should().BeEmpty();

        // 記録のジョブは、同じ対象の計画・登録を止めない
        restarted.Main.Queue.ActiveFor("sub", "rg", "demo").Should().BeNull();
    }

    [Fact]
    public void A_Failed_Expiry_Deletion_Should_Be_Shown_From_The_Record()
    {
        var history = new InMemoryJobHistory
        {
            Records =
            [
                new(JobKind.Delete, "sub", "rg", "old", true, null, _time.Now, DesktopJobState.Failed, "失敗しました", "遅れて実行しました", null, null, null,
                    "削除したもの: なし。", ["09:00:00 始めました", "09:01:00 失敗しました"]),
            ],
        };

        var job = new Harness(time: _time, history: history).Main.Jobs.Single();

        (job.Title, job.KindText, job.StatusText).Should().Be(("期限での削除（rg / old）", "期限での削除", "失敗"));
        (job.Error, job.Note, job.DeletionSummary, job.ResultText).Should().Be(("失敗しました", "遅れて実行しました", "削除したもの: なし。", "失敗しました"));
        (job.Endpoint, job.HasEndpoint, job.IsDeployment).Should().Be((null, false, false));
        job.Logs.Should().Equal("09:00:00 始めました", "09:01:00 失敗しました");
    }

    [Fact]
    public async Task Old_And_Excess_Records_Should_Be_Dropped()
    {
        var stored = new CountingJobHistory().With(
            [Record("expired", _time.Now - JobHistory.RetentionPeriod - TimeSpan.FromMinutes(1)),
             .. Enumerable.Range(0, JobHistory.MaxRecords + 1).Select(i => Record($"app{i}", _time.Now.AddMinutes(-i)))]);

        var h = new Harness(time: _time);
        var main = new MainViewModel(h.SignIn, h.Azure, h.Dialogs, h.Launcher, _time, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            history: stored);

        main.Jobs.Should().HaveCount(JobHistory.MaxRecords);
        main.Jobs[0].TargetText.Should().Be("rg / app0", "新しい順");
        main.Jobs[^1].TargetText.Should().Be($"rg / app{JobHistory.MaxRecords - 1}", "上限を超えた古いものと、期間を過ぎたものは除く");
        stored.Writes.Should().Be(1, "除いたものがあれば、ファイルも書き直す");
        stored.Records.Should().HaveCount(JobHistory.MaxRecords);

        // 除くものが無ければ書き直さない。状態の変更だけでは書かず、終わったものが増えたら書く
        var again = new CountingJobHistory().With(stored.Records);
        var next = new MainViewModel(h.SignIn, h.Azure, h.Dialogs, h.Launcher, _time, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            history: again);
        again.Writes.Should().Be(0);
        var job = next.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "new"), _time.Now.AddHours(1), () => new TaskCompletionSource<Job>().Task);
        await job.PauseCommand.ExecuteAsync();
        again.Writes.Should().Be(0);
        await job.CancelCommand.ExecuteAsync();
        again.Writes.Should().Be(1);
        again.Records.Should().HaveCount(JobHistory.MaxRecords).And.Contain(r => r.AppName == "new");
        again.Records.Should().NotContain(r => r.AppName == $"app{JobHistory.MaxRecords - 1}", "保存するときも上限を守る");
    }

    [Fact]
    public async Task Deleting_Records_Should_Remove_Only_Finished_Jobs_After_Confirming()
    {
        var history = new InMemoryJobHistory { Records = [Record("a", _time.Now.AddDays(-2)), Record("b", _time.Now.AddDays(-1))] };
        var h = await new Harness(time: _time, history: history).ReadyAsync();
        var list = h.Main.JobList;
        var a = h.Main.Jobs.Single(j => j.Target.AppName == "a");
        var b = h.Main.Jobs.Single(j => j.Target.AppName == "b");
        list.DeleteSelectedRecordsCommand.CanExecute(null).Should().BeFalse("選んでいない");
        list.DeleteFinishedRecordsCommand.CanExecute(null).Should().BeTrue();
        var waiting = Pending(h, "c", _time.Now.AddHours(1));
        list.SetSelection([waiting]);
        list.DeleteSelectedRecordsCommand.CanExecute(null).Should().BeFalse("終わっていないものだけ");

        list.SetSelection([a, waiting]);
        list.DeleteSelectedRecordsCommand.CanExecute(null).Should().BeTrue();
        await list.DeleteSelectedRecordsCommand.ExecuteAsync();
        h.Dialogs.Confirmations.Should().Equal("ジョブの記録を削除しますか？");
        h.Dialogs.Messages[0].Should().Be("選んだジョブのうち終わったものの記録 1 件を、ジョブの一覧とこの PC から削除します。元に戻せません（Azure のリソースは削除しません）。");
        h.Main.Jobs.Should().HaveCount(3, "確かめて「いいえ」なら消さない");

        h.Dialogs.Confirm = true;
        await list.DeleteSelectedRecordsCommand.ExecuteAsync();
        h.Main.Jobs.Should().Equal(waiting, b);
        list.Message.Should().Be("1 件の記録を削除しました（1 件は、終わっていないため、そのままです）。");
        history.Records.Select(r => r.AppName).Should().Equal("b");
        list.Selected.Should().Equal(waiting);

        await list.DeleteFinishedRecordsCommand.ExecuteAsync();
        h.Dialogs.Messages[^1].Should().StartWith("終わったジョブすべての記録 1 件を");
        list.Message.Should().Be("1 件の記録を削除しました。");
        h.Main.Jobs.Should().Equal(waiting);
        history.Records.Should().BeEmpty();
        list.DeleteFinishedRecordsCommand.CanExecute(null).Should().BeFalse("終わったものが無い");
        h.Main.Queue.RemoveFinished([b, waiting]).Should().Be(0, "一覧に無いもの・終わっていないものは消さない");
    }

    [Fact]
    public async Task The_All_Checkbox_Should_Select_And_Clear_The_Rows()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var list = h.Main.JobList;
        list.AllSelected.Should().BeFalse("行が無い");
        var a = Pending(h, "a", _time.Now.AddHours(1));
        var b = Pending(h, "b", _time.Now.AddHours(2));
        var raised = new List<string?>();
        list.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        list.AllSelected = true;
        list.Selected.Should().BeEquivalentTo([a, b]);
        list.AllSelected.Should().BeTrue();
        raised.Should().Contain(nameof(JobListViewModel.AllSelected));

        list.SetSelection([a]);
        list.AllSelected.Should().BeNull("一部だけ");
        list.AllSelected = null;
        list.Selected.Should().Equal([a], "中間の値を入れても変えない");

        list.AllSelected = true;
        raised.Clear();
        var c = Pending(h, "c", _time.Now.AddHours(3));
        list.AllSelected.Should().BeNull("増えた行は選んでいない");
        raised.Should().Contain(nameof(JobListViewModel.AllSelected));

        // 絞り込んだときは、表に出ている行だけ
        list.TargetFilter = "c";
        list.AllSelected.Should().BeFalse();
        list.AllSelected = true;
        list.Selected.Should().Equal(c);
        list.AllSelected = false;
        list.Selected.Should().BeEmpty();
    }

    [Fact]
    public async Task The_List_Should_Sort_By_The_Finished_Time()
    {
        var history = new InMemoryJobHistory { Records = [Record("late", _time.Now.AddDays(-1)), Record("early", _time.Now.AddDays(-3))] };
        var h = await new Harness(time: _time, history: history).ReadyAsync();
        var list = h.Main.JobList;
        var active = Pending(h, "active", _time.Now.AddHours(1));
        active.FinishedText.Should().BeNull();
        active.DurationText.Should().BeNull();

        list.SortBy(JobSortKey.Finished);
        list.FinishedHeader.Should().Be("実行終了日時 ▲");
        list.Items.Select(j => j.Target.AppName).Should().Equal("early", "late", "active");
        list.SortBy("Finished");
        list.Items.Select(j => j.Target.AppName).Should().Equal("late", "early", "active");
    }

    [Fact]
    public void The_File_Should_Keep_The_Records_And_Survive_Errors()
    {
        var path = Path.Combine(_dir, "data", "job-history.json");
        new FileJobHistory(path).Records.Should().BeEmpty("ファイルが無ければ空");
        var record = Record("demo", _time.Now) with { ScheduledAt = _time.Now.AddHours(-1), Note = "注意" };

        new FileJobHistory(path) { Records = [record] };

        new FileJobHistory(path).Records.Should().ContainSingle().Which.Should().BeEquivalentTo(record);
        File.ReadAllText(path).Should().Contain("\"Succeeded\"").And.Contain("\"Deploy\"").And.NotContain("\"Target\"");

        File.WriteAllText(path, "{ not json");
        new FileJobHistory(path).Records.Should().BeEmpty();
        File.WriteAllText(path, "null");
        new FileJobHistory(path).Records.Should().BeEmpty();

        // 保存先のフォルダーの名前で、ファイルがある（フォルダーを作れない）
        File.WriteAllText(Path.Combine(_dir, "blocked"), "");
        var blocked = new FileJobHistory(Path.Combine(_dir, "blocked", "job-history.json")) { Records = [record] };
        blocked.Records.Should().Equal(record);
    }
}
