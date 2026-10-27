using FluentAssertions;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using Xunit;
using static TTDeployTool.Controller.Desktop.Core.Tests.DeploymentListViewModelTests;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>
/// 「今すぐ実行・削除」「予定に追加」が押せない理由（No.74）。オーナーの実機（2026-10-27）: 削除の画面で「予定に追加」が押せず、
/// アプリ名を入れていないためと分からなかった。
/// </summary>
public class BlockedTextTests
{
    private readonly MovableTime _time = new();

    private DateTimeOffset Past => _time.GetLocalNow().AddDays(-1);

    [Fact]
    public async Task Deletion_Should_Tell_Why_The_Buttons_Cannot_Be_Pressed()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        (vm.BlockedText, vm.IsBlocked).Should().Be(((string?)null, false), "計画の前は出さない");

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        await vm.PlanCommand.ExecuteAsync();
        vm.BlockedText.Should().Be("アプリ名「demo」を入力すると、「今すぐ削除」と「予定に追加」が押せます。");
        raised.Should().Contain([nameof(DeletionViewModel.BlockedText), nameof(DeletionViewModel.IsBlocked)]);

        vm.ConfirmAppName = "demo";
        (vm.BlockedText, vm.IsBlocked).Should().Be(((string?)null, false));

        raised.Clear();
        vm.Schedule.Date = Past;
        raised.Should().Contain(nameof(DeletionViewModel.BlockedText));
        vm.BlockedText.Should().StartWith("「予定に追加」: 予定の時刻（").And.EndWith("は、今より後にしてください。");
    }

    [Fact]
    public async Task Deployment_Should_Tell_Why_The_Buttons_Cannot_Be_Pressed()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        var vm = h.Main.Deployment;
        vm.BlockedText.Should().BeNull("計画の前は出さない");

        // 権限が無い（計画に誤りがある）
        var full = h.Azure.Permissions[0];
        h.Azure.Permissions[0] = new TTDeployTool.Domain.Azure.PermissionSet(["*/read"], []);
        await vm.PlanCommand.ExecuteAsync();
        vm.BlockedText.Should().Be("計画に誤りがあるため、実行できません。上の赤い行を直して「計画し直す」を押してください。");

        // 既存の DB アプリを更新する（データ消失の同意が要る）
        h.Azure.Permissions[0] = full;
        h.Azure.Deployed();
        await vm.PlanCommand.ExecuteAsync();
        vm.BlockedText.Should().Be("「DB のデータが失われることを理解しました」にチェックを入れると、「今すぐ実行」と「予定に追加」が押せます。");
        vm.DataLossAcknowledged = true;
        (vm.BlockedText, vm.IsBlocked).Should().Be(((string?)null, false));
        vm.Schedule.TimeOfDay = TimeSpan.Zero;
        vm.Schedule.Date = Past;
        vm.IsBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Batch_Deletion_Should_Tell_Why_The_Buttons_Cannot_Be_Pressed()
    {
        var h = new Harness(time: _time);
        h.Azure.RecordsByTenant["t1"] = [.. Deployment("sub", "rg", "demo")];
        h.Azure.Deployed();
        await h.ReadyAsync();
        var vm = h.Main.BatchDeletion;
        vm.BlockedText.Should().BeNull("削除するものが無ければ出さない");

        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        await vm.OpenAsync(h.Main.Deployments.Rows.Select(r => r.Entry).ToList());
        vm.BlockedText.Should().Be("削除する件数（1）を入力すると、「今すぐ削除」と「予定に追加」が押せます。");
        vm.ConfirmCount = "1";
        vm.IsBlocked.Should().BeFalse();
        vm.Schedule.Date = Past;
        vm.BlockedText.Should().StartWith("「予定に追加」: ");
    }
}
