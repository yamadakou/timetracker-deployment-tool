using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>一覧の行の右クリックのメニューの操作と、右の詳細のコピー（オーナーの依頼、2026-11-14）。</summary>
public sealed class RowMenuTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    private static FakeTimeProvider Tokyo()
    {
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TokyoTime.Zone);
        return time;
    }

    private static async Task<Harness> ListedAsync(bool withClipboard = true)
    {
        var h = new Harness(time: Tokyo(), withClipboard: withClipboard);
        h.Azure.RecordsByTenant["t1"] =
        [
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "soon", "2026-11-15T14:59:59Z", deployed: "2026-11-01T00:00:00Z"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "late", "2027-03-01T14:59:59Z"),
            .. DeploymentListViewModelTests.Deployment("sub", "rg", "forever", "none"),
            new ManagedResourceRecord("/subscriptions/sub/resourceGroups/rg-old", "rg-old", ManagedResourceRecord.ResourceGroupType, "sub", "rg-old", "japaneast",
                ManagedTags.ForResourceGroup()),
        ];
        await h.ReadyAsync();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        h.Azure.Apps.Clear();
        foreach (var app in new[] { "soon", "late", "forever" })
            foreach (var suffix in new[] { "tt", "redis", "db" })
                h.Azure.Apps.Add(new ContainerAppInfo($"{app}-{suffix}", FakeAzure.EnvId, ManagedTags.For(app)));
        return h;
    }

    private static RowMenuItem Item(IReadOnlyList<RowMenuItem> items, string id) => items.Single(i => i.Id == id);

    [Fact]
    public async Task A_Deployment_Row_Should_Offer_The_Detail_Changes_And_The_Deletion()
    {
        var h = await ListedAsync();
        var list = h.Main.Deployments;
        var row = list.Rows.Single(r => r.AppName == "soon");
        var items = list.MenuFor([row]);

        items.Select(i => i.Id).Should().Equal("Row.Detail", "Row.Open", "Row.UseAsTarget", "Row.Redeploy", "Row.CheckStatus", "Row.ShowPasswords",
            "Row.Extend7", "Row.Extend30", "Row.EditBatch", "Row.Delete");
        items.Where(i => i.StartsGroup).Select(i => i.Id).Should().Equal("Row.UseAsTarget", "Row.Extend7", "Row.Delete");
        (Item(items, "Row.Delete").IsDanger, Item(items, "Row.Redeploy").IsEnabled, Item(items, "Row.Open").IsEnabled).Should().Be((true, false, true));
        Item(items, "Row.CheckStatus").Label.Should().Be("状態を確認");

        await Item(items, "Row.Detail").Run();
        list.Current.Should().BeSameAs(row);
        await Item(items, "Row.Open").Run();
        h.Launcher.Opened.Should().ContainSingle().Which.Should().Contain("soon-tt");

        await Item(items, "Row.Extend7").Run();
        await Item(items, "Row.Extend30").Run();
        row.Error.Should().BeNull();

        var edits = 0;
        list.BatchEditRequested += () => edits++;
        list.SetSelection([]);
        await Item(items, "Row.EditBatch").Run();
        (edits, list.Current).Should().Be((1, row));

        await Item(items, "Row.CheckStatus").Run();
        await Item(items, "Row.UseAsTarget").Run();
        await Item(items, "Row.Delete").Run();
        h.Navigations.Should().EndWith([Pages.Status, Pages.Wizard, Pages.Delete]);

        list.SetSelection([]);
        h.Azure.GatewayMock.Setup(g => g.GetContainerAppSecretsAsync("sub", "rg", "soon-tt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["db-password"] = "DbPassw0rd!" });
        await Item(items, "Row.ShowPasswords").Run();
        (list.Current, row.HasPasswords).Should().Be((row, true));
    }

    [Fact]
    public async Task Several_Rows_Should_Be_Extended_Changed_And_Deleted_Together()
    {
        var h = await ListedAsync();
        var list = h.Main.Deployments;
        var rows = list.Rows.Where(r => r.AppName is "soon" or "late").ToList();
        var items = list.MenuFor(rows);

        items.Select(i => (i.Id, i.Label)).Should().Equal(
            ("Rows.Extend7", "選んだ 2 件の期限を 7 日延ばす"), ("Rows.Extend30", "選んだ 2 件の期限を 30 日延ばす"),
            ("Rows.EditBatch", "選んだ 2 件の期限・グループを変える…"), ("Rows.Delete", "選んだ 2 件を Azure から削除…"));
        await Item(items, "Rows.Extend7").Run();
        list.BatchMessage.Should().StartWith("2 件の利用可能期限を 7 日延ばしました（");

        // ジョブのある行と、リソースグループだけの行は飛ばす
        h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "late"), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task);
        var mixed = list.Rows.Where(r => r.AppName != "forever").ToList();
        await Item(list.MenuFor(mixed), "Rows.Extend30").Run();
        list.BatchMessage.Should().Be("1 件の利用可能期限を 30 日延ばしました（soon）。 延ばせなかったもの・飛ばしたもの: 2 件。");
        var stuck = list.Rows.Where(r => r.AppName is "late" or "").ToList();
        Item(list.MenuFor(stuck), "Rows.Extend7").IsEnabled.Should().BeFalse();
        await Item(list.MenuFor(stuck), "Rows.Extend7").Run();
        list.BatchMessage.Should().Be("0 件の利用可能期限を 7 日延ばしました。 延ばせなかったもの・飛ばしたもの: 2 件。");

        var edits = 0;
        list.BatchEditRequested += () => edits++;
        await Item(items, "Rows.EditBatch").Run();
        (edits, list.Selected.Count).Should().Be((1, 2));
        await Item(items, "Rows.Delete").Run();
        h.Navigations.Should().EndWith(Pages.BatchDelete);
        list.MenuFor([]).Should().BeEmpty();
    }

    [Fact]
    public async Task The_Detail_Should_Be_Copied_Whole_Or_As_Selected()
    {
        var h = await ListedAsync();
        var list = h.Main.Deployments;
        var row = list.Rows.Single(r => r.AppName == "soon");

        row.DetailText.Split('\n').Should().StartWith(["アプリ名: soon", "リソースグループ: rg"])
            .And.Contain(l => l.StartsWith("利用可能期限: 2026-11-15") && l.EndsWith("、あと 3 日"))
            .And.Contain(l => l.StartsWith("URL: https://soon-tt"))
            .And.Contain(l => l.StartsWith("料金の目安: 約 "));
        await row.CopyDetailCommand.ExecuteAsync();
        (h.Clipboard.Copied[^1], row.Message).Should().Be((row.DetailText, "詳細をコピーしました。"));
        await row.CopySelectedTextAsync("soon-tt");
        (h.Clipboard.Copied[^1], row.Message).Should().Be(("soon-tt", "選んだ文字をコピーしました。"));
        await row.CopySelectedTextAsync("");
        h.Clipboard.Copied.Should().HaveCount(2, "選んでいなければコピーしない");

        var group = list.Rows.Single(r => r.AppName == "");
        group.DetailText.Split('\n').Should().Equal("アプリ名: （リソースグループだけ）", "リソースグループ: rg-old", $"サブスクリプション: {group.Subscription}", $"状態: {group.StateText}");
        var late = list.Rows.Single(r => r.AppName == "late");
        late.DetailText.Should().Contain("利用可能期限: 2027-03-01、あと ");
        list.Rows.Single(r => r.AppName == "forever").DetailText.Should().Contain("利用可能期限: 期限なし\n");
        row.CopyDetailCommand.Should().BeSameAs(row.CopyDetailCommand);

        var without = (await ListedAsync(withClipboard: false)).Main.Deployments.Rows.First(r => r.AppName == "soon");
        without.CopyDetailCommand.CanExecute(null).Should().BeFalse();
        await without.CopySelectedTextAsync("x");
        without.Message.Should().BeNull();
    }

    // ---------------- ジョブの一覧

    [Fact]
    public async Task A_Job_Row_Should_Offer_The_Detail_And_The_Job_Operations()
    {
        var h = await new Harness(time: Tokyo()).ReadyAsync();
        var job = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "app"), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task);
        var list = h.Main.JobList;
        var opened = new List<JobViewModel>();
        Task Open(JobViewModel j)
        {
            opened.Add(j);
            return Task.CompletedTask;
        }

        var items = list.MenuFor([job], Open, signedIn: true);
        items.Select(i => i.Id).Should().Equal("Job.Detail", "Job.Redeploy", "Job.ChangeDeletion", "Jobs.Cancel", "Jobs.Pause", "Jobs.Resume", "Jobs.Reschedule", "Jobs.DeleteRecords");
        items.Select(i => i.Label).Should().Contain(["取り消し", "予定を変える…", "記録から削除"]);
        (Item(items, "Job.Redeploy").IsEnabled, Item(items, "Jobs.Cancel").IsEnabled, Item(items, "Jobs.Reschedule").IsEnabled, Item(items, "Jobs.DeleteRecords").IsEnabled)
            .Should().Be((false, true, true, false));

        await Item(items, "Job.Detail").Run();
        await Item(items, "Job.ChangeDeletion").Run();
        opened.Should().Equal(job, job);
        var reschedules = 0;
        list.RescheduleRequested += () => reschedules++;
        await Item(items, "Jobs.Reschedule").Run();
        (reschedules, list.Current).Should().Be((1, job));
        await Item(items, "Jobs.Pause").Run();
        job.State.Should().Be(DesktopJobState.Paused);
        await Item(list.MenuFor([job], Open, true), "Jobs.Resume").Run();
        await Item(list.MenuFor([job], Open, true), "Jobs.Cancel").Run();
        job.State.Should().Be(DesktopJobState.Canceled);
        h.Dialogs.Confirm = true;
        await Item(list.MenuFor([job], Open, true), "Jobs.DeleteRecords").Run();
        h.Main.Queue.Jobs.Should().NotContain(job);

        // サインインしていなければ、詳細だけ（削除の設定も変えられない）
        var deletion = h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", "other"), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task);
        Item(list.MenuFor([deletion], Open, signedIn: false), "Job.ChangeDeletion").IsEnabled.Should().BeFalse();
        var other = h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "next"), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task);
        var signedOut = list.MenuFor([other], Open, signedIn: false);
        signedOut.Where(i => i.IsEnabled).Select(i => i.Id).Should().Equal("Job.Detail");
        signedOut.Select(i => i.Id).Should().NotContain("Job.ChangeDeletion", "デプロイのジョブには削除の設定が無い");
        await Item(signedOut, "Job.Redeploy").Run();
    }

    [Fact]
    public async Task Several_Job_Rows_Should_Be_Operated_Together()
    {
        var h = await new Harness(time: Tokyo()).ReadyAsync();
        var jobs = new[] { "a", "b" }.Select(app =>
            h.Main.Queue.Register(JobKind.Delete, new JobTarget("sub", "rg", app), Now.AddDays(1), () => new TaskCompletionSource<Job>().Task)).ToList();
        var items = h.Main.JobList.MenuFor(jobs, _ => Task.CompletedTask, signedIn: true);

        items.Select(i => i.Label).Should().Equal("選んだ 2 件を取り消し", "選んだ 2 件を一時停止", "選んだ 2 件を再開", "選んだ 2 件の予定を変える…", "選んだ 2 件を記録から削除");
        await Item(items, "Jobs.Cancel").Run();
        jobs.Should().OnlyContain(j => j.State == DesktopJobState.Canceled);
        h.Main.JobList.MenuFor([], _ => Task.CompletedTask, true).Should().BeEmpty();
    }
}
