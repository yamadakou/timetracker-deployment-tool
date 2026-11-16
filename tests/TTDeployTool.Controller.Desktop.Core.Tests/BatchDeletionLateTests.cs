using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Jobs;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;
using static TTDeployTool.Controller.Desktop.Core.Tests.DeploymentListViewModelTests;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// まとめての削除で、待っている間に計画の有効期限（15 分）が切れても、1 件目の後の 2 件目以降も削除できる（オーナーの実機で、2 件目以降が
/// 「計画が見つかりません」で失敗した。2026-11-16）。1 件目が計画し直して保存すると、期限の切れた 2 件目の計画が片付けられ、「見つからない」になっていた。
/// </summary>
public sealed class BatchDeletionLateTests
{
    [Fact]
    public async Task Every_Row_Of_A_Waiting_Batch_Should_Be_Deleted_After_The_Plans_Expire()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
        var h = new Harness(time: time);
        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "demo"), .. Deployment("sub", "rg", "other"), .. Deployment("sub", "rg", "third")];
        h.Azure.Deployed();
        foreach (var app in new[] { "other", "third" })
            foreach (var suffix in new[] { "-tt", "-db", "-redis" })
                h.Azure.Apps.Add(new ContainerAppInfo(app + suffix, FakeAzure.EnvId, ManagedTags.For(app)));
        await h.ReadyAsync();
        var list = h.Main.Deployments;
        await list.LoadCommand.ExecuteAsync();
        list.SetSelection(list.Rows);
        await list.DeleteSelectedCommand.ExecuteAsync();
        var batch = h.Main.BatchDeletion;
        batch.RunnableCount.Should().Be(3);

        // 30 分後に予定し、1 件ずつ実行する（予定の時刻には、3 件とも計画の有効期限が切れている）
        h.Main.Queue.MaxConcurrency = 1;
        batch.ConfirmCount = "3";
        batch.Schedule.Date = time.GetUtcNow();
        batch.Schedule.TimeOfDay = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).TimeOfDay + TimeSpan.FromMinutes(30);
        await batch.ScheduleCommand.ExecuteAsync();
        var jobs = h.Main.Queue.Jobs.ToList();
        jobs.Should().HaveCount(3).And.OnlyContain(j => j.State == DesktopJobState.Scheduled);

        time.Advance(TimeSpan.FromMinutes(31));
        // ジョブの進み具合は、止めた時刻で読むため、少しずつ進めて待つ
        for (var i = 0; i < 300 && jobs.Any(j => j.IsActive); i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        jobs.Select(j => (j.Target.AppName, j.State, j.Error)).Should().OnlyContain(j => j.State == DesktopJobState.Succeeded, "1 件目が計画し直した後も、2 件目以降は計画し直して削除する");
    }
}
