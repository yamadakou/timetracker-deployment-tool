using Azure;
using FluentAssertions;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;
using static TTDeployTool.Controller.Desktop.Core.Tests.DeploymentListViewModelTests;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>デプロイのグループ（docs/design/common/deployment-groups.md の 4・5）。</summary>
public class GroupTests
{
    private readonly MovableTime _time = new();

    // グループのタグを Timetracker と DB の Container App に付けたデプロイ（db を別の値にすると食い違い）
    private static IEnumerable<ManagedResourceRecord> Grouped(string app, string? group, string? dbGroup = null, string rg = "rg") =>
        Deployment("sub", rg, app).Select(r =>
            (r.Type, r.Name.EndsWith("-db", StringComparison.Ordinal) ? dbGroup ?? group : group) is (ManagedResourceRecord.ContainerAppType, { } value)
                ? r with { Tags = With(r.Tags, value) }
                : r);

    private static Dictionary<string, string> With(IReadOnlyDictionary<string, string> tags, string group) =>
        new(tags, StringComparer.OrdinalIgnoreCase) { [DeploymentGroup.Key] = group };

    private async Task<Harness> LoadedAsync(params IEnumerable<ManagedResourceRecord>[] deployments)
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Azure.RecordsByTenant["t1"] = deployments.SelectMany(d => d).ToList();
        await h.Main.Deployments.LoadCommand.ExecuteAsync();
        return h;
    }

    private static string Names(DeploymentListViewModel vm) => string.Join(",", vm.Rows.Select(r => r.AppName));

    [Fact]
    public async Task The_Settings_Should_Pass_The_Group_And_Keep_It_In_Templates()
    {
        var h = await LoadedAsync(Grouped("a", "営業"), Grouped("b", "研修"), Grouped("c", "Sales"));
        var settings = h.Main.Settings;
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.Group = " 研 修 ";
        raised.Should().Contain(nameof(SettingsViewModel.GroupSuggestions));
        settings.Group.Should().Be(" 研 修 ", "入力のまま持ち、途中の空白も入れられる");
        settings.Build(ValidationTarget.Azure).Options!.Group.Should().Be("研 修");
        settings.ToMarkdown(false).Should().Contain("| グループ | 研 修 |");

        // 候補は、一覧のグループのうち入力を含むもの（大文字と小文字を区別しない。入力と同じものは出さない）
        settings.Group = "s";
        settings.GroupSuggestions.Should().Equal("Sales");
        settings.Group = "";
        settings.GroupSuggestions.Should().Equal("Sales", "営業", "研修");
        settings.Group = "営業";
        settings.GroupSuggestions.Should().BeEmpty();

        settings.Group = new string('x', 65);
        settings.Errors.Should().Contain("group（グループ名）は 64 文字までにしてください（65 文字）。");

        settings.Group = "研修";
        settings.TemplateName = "研修用";
        await settings.SaveTemplateCommand.ExecuteAsync();
        h.Preferences.Templates.Single().Group.Should().Be("研修");
        settings.Group = "";
        settings.ToMarkdown(false).Should().Contain("| グループ | （なし。再デプロイでは今のグループを引き継ぐ） |");
        settings.SelectedTemplate = settings.Templates.Single();
        await settings.ApplyTemplateCommand.ExecuteAsync();
        settings.Group.Should().Be("研修");
        settings.SelectedTemplate = settings.Templates.Single() with { Group = null };
        await settings.ApplyTemplateCommand.ExecuteAsync();
        settings.Group.Should().BeEmpty("グループの無い（古い）テンプレート");

        settings.Group = "営業";
        await settings.ResetToDefaultsAsync(confirm: false);
        settings.Group.Should().BeEmpty();
        SettingsViewModel.GroupNotice.Should().Contain("64 文字まで").And.Contain("平文");
        new SettingsViewModel(h.Main.Target).GroupSuggestions.Should().BeEmpty("候補を渡していない");
    }

    [Fact]
    public async Task The_Plan_Should_Show_The_Group_To_Record()
    {
        var h = await new Harness(time: _time).ReadyAsync();
        h.Main.Deployment.GroupText.Should().BeNull("計画の前");
        h.Main.Settings.Group = "研修";

        await h.Main.Deployment.PlanCommand.ExecuteAsync();

        h.Main.Deployment.GroupText.Should().Be("グループ: 研修");
    }

    [Fact]
    public async Task The_List_Should_Show_Sort_And_Filter_By_Group()
    {
        var h = await LoadedAsync(Grouped("a", "営業"), Grouped("b", "研修", dbGroup: "営業"), Grouped("c", null), Grouped("d", "eigyo"), Grouped("e", "EIGYO"));
        var vm = h.Main.Deployments;
        var b = vm.Rows.Single(r => r.AppName == "b");
        (b.GroupText, b.GroupWarning).Should().Be(("研修（食い違い）", b.GroupWarning));
        b.GroupWarning.Should().StartWith("リソースごとにグループのタグ");
        var a = vm.Rows.Single(r => r.AppName == "a");
        (a.GroupText, a.GroupWarning).Should().Be(("営業", null));
        vm.Rows.Single(r => r.AppName == "c").GroupText.Should().BeEmpty();

        vm.GroupNames.Should().Equal(["eigyo", "営業", "研修"], "大文字と小文字は区別せずに束ね、最初の書き方を出す");
        vm.Filters.ValuesOf("Group").Select(v => (v.Value, v.Count)).Should().Equal(("eigyo", 2), ("営業", 1), ("研修", 1), ("（グループなし）", 1));

        vm.SortBy(DeploymentSortKey.Group);
        (Names(vm), vm.SortColumn).Should().Be(("d,e,a,b,c", "Group"), "グループの無い行は最後");
        vm.SortBy("Group");
        Names(vm).Should().Be("b,a,d,e,c");

        vm.Filters.SetValues("Group", ["EIGYO"]);
        Names(vm).Should().Be("d,e", "大文字と小文字を区別しない");
        vm.Filters.SetValues("Group", ["（グループなし）"]);
        Names(vm).Should().Be("c");
        vm.Filters.SetValues("Group", ["研修"]);

        // 読み直しても、選んでいたグループで絞り込んだまま（無くなれば、どの行も出さない）
        await vm.LoadCommand.ExecuteAsync();
        Names(vm).Should().Be("b");
        h.Azure.RecordsByTenant["t1"] = [.. Grouped("a", "営業")];
        await vm.LoadCommand.ExecuteAsync();
        Names(vm).Should().BeEmpty();
        vm.Filters.ValuesOf("Group").Should().Equal(new FilterValue("営業", 1, false));
        vm.Filters.ClearValues("Group");
        Names(vm).Should().Be("a");

        // 文字のフィルタは、セルに見えている文字（グループの無い行は空）で探す
        vm.Filters.TextTarget = vm.Filters.TextTargets.Single(t => t.Label == "グループ");
        vm.Filters.Text = "なし";
        Names(vm).Should().BeEmpty();
    }

    [Fact]
    public async Task The_Selected_Groups_Should_Change_Together_After_Confirming()
    {
        var h = await LoadedAsync(Grouped("demo", "営業"), Grouped("busy", null), Grouped("gone", null));
        var tagged = new List<string>();
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => tagged.Add($"merge {n} {t[DeploymentGroup.Key]}"))
            .Returns(Task.CompletedTask);
        h.Azure.GatewayMock.Setup(g => g.DeleteContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, n, t, _) => tagged.Add($"delete {n} {t[DeploymentGroup.Key]}"))
            .Returns(Task.CompletedTask);
        h.Azure.Apps.Add(new ContainerAppInfo("demo-tt", FakeAzure.EnvId, new Dictionary<string, string>(FakeAzure.Managed) { [DeploymentGroup.Key] = "営業" }));
        h.Main.Queue.Register(JobKind.Deploy, new JobTarget("sub", "rg", "busy"), _time.GetUtcNow().AddHours(1), () => new TaskCompletionSource<Job>().Task);
        var vm = h.Main.Deployments;
        vm.ChangeSelectedGroupCommand.CanExecute(null).Should().BeFalse();
        vm.RemoveSelectedGroupCommand.CanExecute(null).Should().BeFalse();
        vm.SetSelection(vm.Rows);
        vm.ChangeSelectedGroupCommand.CanExecute(null).Should().BeFalse("グループ名が空");
        vm.RemoveSelectedGroupCommand.CanExecute(null).Should().BeTrue();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.BatchGroup = "営";
        vm.BatchGroup = "営";
        raised.Count(n => n == nameof(DeploymentListViewModel.BatchGroupSuggestions)).Should().Be(1, "同じ値なら知らせない");
        vm.BatchGroupSuggestions.Should().Equal("営業");
        vm.BatchGroup = " 研修 ";
        vm.ChangeSelectedGroupCommand.CanExecute(null).Should().BeTrue();

        // 確かめて「いいえ」なら変えない
        await vm.ChangeSelectedGroupCommand.ExecuteAsync();
        h.Dialogs.Confirmations.Should().Equal("グループを変えますか？");
        h.Dialogs.Messages[0].Should().Be("選んだ 3 件のデプロイのグループを「研修」にします（Azure のタグ tt-group を書き換えます。ジョブがあるデプロイは変えません）。");
        tagged.Should().BeEmpty();

        h.Dialogs.Confirm = true;
        await vm.ChangeSelectedGroupCommand.ExecuteAsync();
        tagged.Should().Equal("merge demo-tt 研修");
        vm.BatchMessage.Should().StartWith("1 件のグループを「研修」にしました（demo）。 ジョブがあるため変えなかったもの: busy。 変えられなかったもの: gone（リソースグループ rg に、アプリ名 gone の");
        var demo = vm.Rows.Single(r => r.AppName == "demo");
        (demo.GroupText, demo.Message).Should().Be(("研修", "グループを「研修」にしました。"));
        vm.GroupNames.Should().Contain("研修", "変えた値を、一覧を読み直さずに重ねる");

        // 外す
        tagged.Clear();
        vm.SetSelection(vm.Rows.Where(r => r.AppName == "demo"));
        await vm.RemoveSelectedGroupCommand.ExecuteAsync();
        h.Dialogs.Confirmations[^1].Should().Be("グループを外しますか？");
        h.Dialogs.Messages[^1].Should().Be("選んだ 1 件のデプロイのグループを外します（Azure のタグ tt-group を書き換えます。ジョブがあるデプロイは変えません）。");
        tagged.Should().Equal("delete demo-tt 営業");
        vm.BatchMessage.Should().Be("1 件のグループを外しました（demo）。");
        vm.Rows.Single(r => r.AppName == "demo").Message.Should().Be("グループを外しました。");

        // Azure の誤り
        h.Azure.GatewayMock.Setup(g => g.UpdateContainerAppTagsAsync("sub", "rg", It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Forbidden"));
        await vm.ChangeSelectedGroupCommand.ExecuteAsync();
        vm.BatchMessage.Should().StartWith("変えられなかったもの: demo（");

        // 長すぎる名前は、確かめる前に止める
        h.Dialogs.Confirmations.Clear();
        vm.BatchGroup = new string('x', 65);
        await vm.ChangeSelectedGroupCommand.ExecuteAsync();
        vm.BatchMessage.Should().Be("group（グループ名）は 64 文字までにしてください（65 文字）。");
        h.Dialogs.Confirmations.Should().BeEmpty();
    }
}
