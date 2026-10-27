using FluentAssertions;
using Moq;
using TTDeployTool.Application.Jobs;
using TTDeployTool.Controller.Desktop.Core.Services;
using TTDeployTool.Controller.Desktop.Core.ViewModels;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

/// <summary>設定の Markdown のコピー・テンプレート・初期値に戻す（docs/design/desktop-settings.md）。</summary>
public sealed class SettingsToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tt-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static async Task<Harness> ReadyAsync(bool withClipboard = true) =>
        await new Harness(time: new TokyoTime(), withClipboard: withClipboard).ReadyAsync();

    private static DeploymentTemplate Template(string name, string? subscription = "sub", ExpiryMode expiry = ExpiryMode.Default, string? date = null) => new(
        name, subscription, "rg-t", "tmpl", "eastus", "sqlserver", "db1", "11.0.1-linux-mssql", "2", "4", "1.25", "2.5", "0.75", "1.5", true, expiry, date);

    [Fact]
    public async Task The_Markdown_Should_Mask_Passwords_Unless_Asked()
    {
        var h = await ReadyAsync();
        var vm = h.Main.Settings;
        vm.TtTag = "custom|tag";
        vm.Expiry = SettingsViewModel.ExpiryOptions[1];
        vm.ExpiryDate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));

        var masked = vm.ToMarkdown(includePasswords: false);

        h.Main.Target.SubscriptionDisplay.Should().Be("検証（sub、開発）");
        masked.Should().StartWith("# TTDeployTool のデプロイの設定\n\n| 項目 | 値 |\n|---|---|\n")
            .And.Contain("| サブスクリプション | 検証（sub、開発） |")
            .And.Contain("| リソースグループ | rg |")
            .And.Contain("| Timetracker のイメージのタグ | custom\\|tag |")
            .And.Contain("| DB のパスワード | ********（入力済み） |")
            .And.Contain("| Timetracker の CPU / メモリ | 1.5 vCPU / 3.0 Gi |")
            .And.Contain("| 利用可能期限日 | 2026-12-01（期限日の終わり） |")
            .And.Contain("| 作るリソース | demo-env、demo-tt、demo-db、demo-redis |")
            .And.NotContain("DbPassw0rd!")
            // タグが DB 種別に合わないため、誤りの節も出る（「|」は置き換える）
            .And.Contain("## 誤り\n\n- 不適切な組み合わせ: db-type='postgresql' と tt-tag='custom\\|tag'");
        vm.ToMarkdown(includePasswords: true).Should().Contain("| DB のパスワード | DbPassw0rd! |").And.Contain("| Timetracker のパスワード | TrackerPass! |");
    }

    [Fact]
    public void The_Markdown_Should_Show_Empty_Inputs_Errors_And_Warnings()
    {
        var vm = new Harness(time: new TokyoTime()).Main.Settings;
        vm.DbType = "sqlserver";
        vm.TtMemoryGi = "2";

        var markdown = vm.ToMarkdown(includePasswords: true);

        new Harness().Main.Target.SubscriptionDisplay.Should().StartWith("サブスクリプション: 「サインインと対象」で選んでください");
        markdown.Should().Contain("| サブスクリプション | （未選択） |")
            .And.Contain("| リソースグループ | （未入力） |")
            .And.Contain("| DB のパスワード | （未入力） |")
            .And.Contain($"| Timetracker のイメージのタグ | {Defaults.TimetrackerTagSqlServer}（既定） |")
            .And.Contain($"| 利用可能期限日 | {SettingsViewModel.ExpiryOptions[0].Label} |")
            .And.Contain("## 誤り\n\n- subscription は必須です。")
            .And.Contain("## 警告\n\n- ");
        vm.Expiry = SettingsViewModel.ExpiryOptions[2];
        vm.ToMarkdown(false).Should().Contain("| 利用可能期限日 | 期限なし |");
    }

    [Fact]
    public async Task Copying_Should_Put_The_Markdown_On_The_Clipboard()
    {
        var h = await ReadyAsync();
        var vm = h.Main.Settings;

        await vm.CopyMarkdownCommand.ExecuteAsync();
        vm.Message.Should().Be("設定を Markdown でコピーしました（パスワードは伏せ字です）。");
        vm.IncludePasswordsInCopy = true;
        await vm.CopyMarkdownCommand.ExecuteAsync();

        h.Clipboard.Copied.Should().HaveCount(2);
        h.Clipboard.Copied[0].Should().NotContain("DbPassw0rd!");
        h.Clipboard.Copied[1].Should().Contain("DbPassw0rd!");
        vm.Message.Should().StartWith("設定を Markdown でコピーしました（パスワードを含みます。");
    }

    [Fact]
    public async Task Without_A_Clipboard_Nothing_Can_Be_Copied()
    {
        var h = await ReadyAsync(withClipboard: false);
        h.Azure.Deployed();
        await h.Main.Status.RefreshCommand.ExecuteAsync();
        await h.Main.Deletion.PlanCommand.ExecuteAsync();

        h.Main.Settings.CopyMarkdownCommand.CanExecute(null).Should().BeFalse();
        h.Main.Status.CopyCommand.CanExecute(null).Should().BeFalse();
        h.Main.Deletion.CopyPlanCommand.CanExecute(null).Should().BeFalse();
        h.Main.Deletion.CopyAppNameCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task A_Template_Should_Be_Saved_Without_Passwords_And_Applied()
    {
        var h = await ReadyAsync();
        var vm = h.Main.Settings;
        vm.SaveTemplateCommand.CanExecute(null).Should().BeFalse("名前が無ければ保存できない");
        vm.DbType = "sqlserver";
        vm.PersistDb = SettingsViewModel.PersistDbOptions[1];
        vm.Expiry = SettingsViewModel.ExpiryOptions[1];
        vm.ExpiryDate = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9));
        vm.TemplateName = "  検証用  ";

        await vm.SaveTemplateCommand.ExecuteAsync();

        var saved = h.Preferences.Templates.Should().ContainSingle().Subject;
        saved.Should().Be(new DeploymentTemplate("検証用", "sub", "rg", "demo", "japaneast", "sqlserver", "timetracker", "", "1.5", "3.0", "1.0", "2.0", "0.5", "1.0",
            true, ExpiryMode.Date, "2026-12-01"));
        vm.Templates.Should().Equal(saved);
        vm.SelectedTemplate.Should().Be(saved);
        vm.Message.Should().Be("テンプレート「検証用」を保存しました（パスワードは保存しません）。");

        // 入力を変えてから、テンプレートを入れ直す（パスワードはそのまま）
        await ResetConfirmed(h);
        vm.DbPassword = "Other1Pass!";
        await vm.ApplyTemplateCommand.ExecuteAsync();

        (h.Main.Target.ResourceGroup, h.Main.Target.AppName, h.Main.Target.Location).Should().Be(("rg", "demo", "japaneast"));
        (vm.DbType, vm.PersistDb.Value, vm.Expiry.Mode).Should().Be(("sqlserver", (bool?)true, ExpiryMode.Date));
        vm.ExpiryDate.Should().Be(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(9)));
        vm.DbPassword.Should().Be("Other1Pass!");
        vm.Message.Should().Be("テンプレート「検証用」を入力しました（パスワードはそのままです）。");
    }

    private static async Task ResetConfirmed(Harness h)
    {
        h.Dialogs.Confirm = true;
        await h.Main.Settings.ResetCommand.ExecuteAsync();
        h.Dialogs.Confirm = false;
    }

    [Fact]
    public async Task A_Template_Should_Select_Its_Subscription_Or_Tell_It_Is_Missing()
    {
        var h = new Harness(time: new TokyoTime());
        h.Azure.SubscriptionsByTenant["t1"].Add(new SubscriptionInfo("sub2", "本番", "Enabled", "t1"));
        await h.ReadyAsync();
        // 保存してあるテンプレートを、起動したときに読む
        h.Preferences.Templates = [Template("b: 本番", "sub2"), Template("a: 消えた", "gone"), Template("c: 指定なし", null, ExpiryMode.Date, "bad")];
        var vm = new SettingsViewModel(h.Main.Target, new TokyoTime(), h.Preferences, h.Dialogs, h.Clipboard);
        vm.Templates.Select(t => t.Name).Should().Equal("a: 消えた", "b: 本番", "c: 指定なし");
        vm.ApplyTemplateCommand.CanExecute(null).Should().BeFalse();
        vm.DeleteTemplateCommand.CanExecute(null).Should().BeFalse();

        vm.SelectedTemplate = vm.Templates[1];
        await vm.ApplyTemplateCommand.ExecuteAsync();
        h.Main.Target.Subscription.Should().Be("sub2");
        (h.Main.Target.ResourceGroup, h.Main.Target.AppName, h.Main.Target.Location).Should().Be(("rg-t", "tmpl", "eastus"));
        (vm.TtCpu, vm.TtMemoryGi, vm.DbCpu, vm.DbMemoryGi, vm.RedisCpu, vm.RedisMemoryGi).Should().Be(("2", "4", "1.25", "2.5", "0.75", "1.5"));
        (vm.DbName, vm.TtTag).Should().Be(("db1", "11.0.1-linux-mssql"));

        vm.SelectedTemplate = vm.Templates[0];
        await vm.ApplyTemplateCommand.ExecuteAsync();
        h.Main.Target.Subscription.Should().Be("sub2");
        vm.Message.Should().Contain("サブスクリプション gone が一覧に無いため、サブスクリプションは変えていません");

        // サブスクリプションを持たないテンプレートと、読めない日付
        var before = vm.ExpiryDate;
        vm.SelectedTemplate = vm.Templates[2];
        await vm.ApplyTemplateCommand.ExecuteAsync();
        h.Main.Target.Subscription.Should().Be("sub2");
        vm.Expiry.Mode.Should().Be(ExpiryMode.Date);
        vm.ExpiryDate.Should().Be(before);
    }

    [Fact]
    public async Task Overwriting_And_Deleting_A_Template_Should_Be_Confirmed()
    {
        var h = await ReadyAsync();
        var vm = h.Main.Settings;
        vm.TemplateName = "共通";
        await vm.SaveTemplateCommand.ExecuteAsync();
        vm.DbName = "changed";
        vm.TemplateName = "共通";

        await vm.SaveTemplateCommand.ExecuteAsync();
        h.Preferences.Templates.Single().DbName.Should().Be("timetracker", "確認で取り消した");
        h.Dialogs.Confirmations.Should().Equal("テンプレートを上書きしますか？");

        h.Dialogs.Confirm = true;
        await vm.SaveTemplateCommand.ExecuteAsync();
        h.Preferences.Templates.Single().DbName.Should().Be("changed");
        vm.TemplateName = "別";
        await vm.SaveTemplateCommand.ExecuteAsync();
        h.Preferences.Templates.Select(t => t.Name).Should().Equal("共通", "別");
        vm.SelectedTemplate = vm.Templates[0];
        vm.SelectedTemplate = vm.Templates[0];

        h.Dialogs.Confirm = false;
        await vm.DeleteTemplateCommand.ExecuteAsync();
        vm.Templates.Should().HaveCount(2);
        h.Dialogs.Confirm = true;
        await vm.DeleteTemplateCommand.ExecuteAsync();
        vm.Templates.Select(t => t.Name).Should().Equal("別");
        h.Preferences.Templates.Select(t => t.Name).Should().Equal("別");
        vm.SelectedTemplate.Should().BeNull();
        vm.Message.Should().Be("テンプレート「共通」を削除しました。");
    }

    [Fact]
    public async Task Without_Dialogs_Templates_Should_Be_Overwritten_And_Deleted_Directly()
    {
        var h = await ReadyAsync();
        var preferences = new InMemoryAppPreferences { Templates = [Template("x")] };
        var vm = new SettingsViewModel(h.Main.Target, new TokyoTime(), preferences);
        vm.TemplateName = "X";

        await vm.SaveTemplateCommand.ExecuteAsync();
        preferences.Templates.Should().ContainSingle().Which.Name.Should().Be("X");

        await vm.DeleteTemplateCommand.ExecuteAsync();
        preferences.Templates.Should().BeEmpty();
        vm.DbName = "x";
        await vm.ResetCommand.ExecuteAsync();
        vm.DbName.Should().Be(Defaults.DbName);
    }

    [Fact]
    public async Task Reset_Should_Restore_Every_Input_After_Confirmation()
    {
        var h = await ReadyAsync();
        var vm = h.Main.Settings;
        h.Main.Target.Location = "eastus";
        vm.DbType = "sqlserver";
        vm.DbName = "x";
        vm.TtTag = "t";
        vm.TtCpu = "2"; vm.TtMemoryGi = "4"; vm.DbCpu = "2"; vm.DbMemoryGi = "4"; vm.RedisCpu = "1"; vm.RedisMemoryGi = "2";
        vm.PersistDb = SettingsViewModel.PersistDbOptions[1];
        vm.Expiry = SettingsViewModel.ExpiryOptions[2];
        vm.ExpiryDate = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromHours(9));
        vm.IncludePasswordsInCopy = true;

        await vm.ResetCommand.ExecuteAsync();
        vm.DbName.Should().Be("x", "確認で取り消した");
        h.Dialogs.Confirmations.Should().Equal("初期値に戻しますか？");

        h.Dialogs.Confirm = true;
        await vm.ResetCommand.ExecuteAsync();

        h.Main.Target.Subscription.Should().Be("sub", "サブスクリプションの選択は変えない");
        (h.Main.Target.ResourceGroup, h.Main.Target.AppName, h.Main.Target.Location).Should().Be(("", "timetracker", "japaneast"));
        (vm.DbType, vm.DbName, vm.TtTag, vm.DbPassword, vm.TrackerPassword).Should().Be(("postgresql", "timetracker", "", "", ""));
        (vm.TtCpu, vm.TtMemoryGi, vm.DbCpu, vm.DbMemoryGi, vm.RedisCpu, vm.RedisMemoryGi).Should().Be(("1.5", "3.0", "1.0", "2.0", "0.5", "1.0"));
        (vm.PersistDb, vm.Expiry, vm.IncludePasswordsInCopy).Should().Be((SettingsViewModel.PersistDbOptions[0], SettingsViewModel.ExpiryOptions[0], false));
        vm.ExpiryDate.Should().Be(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(9)));
        vm.Message.Should().Be("初期値に戻しました。");
    }

    [Fact]
    public void File_Preferences_Should_Save_And_Read_Back_Templates()
    {
        var path = Path.Combine(_dir, "preferences.json");
        new FilePreferences(path).Templates.Should().BeEmpty();

        new FilePreferences(path) { MaxConcurrentJobs = 3 }.Templates = [Template("a", expiry: ExpiryMode.Date, date: "2026-12-01")];

        var read = new FilePreferences(path);
        read.Templates.Should().Equal(Template("a", expiry: ExpiryMode.Date, date: "2026-12-01"));
        read.MaxConcurrentJobs.Should().Be(3);
        File.ReadAllText(path).Should().NotContain("Password");
        read.ExpiryNotices.Should().BeEmpty();
        read.StartupTaskAsked.Should().BeFalse();

        // 期限の前の通知の記録と、サインイン時の起動を尋ねたこと（6.5）
        read.ExpiryNotices = ["sub/rg/demo/3d/2026-11-23T14:59:59Z"];
        read.StartupTaskAsked = true;
        var again = new FilePreferences(path);
        again.ExpiryNotices.Should().Equal("sub/rg/demo/3d/2026-11-23T14:59:59Z");
        again.StartupTaskAsked.Should().BeTrue();
        again.Templates.Should().HaveCount(1);
    }
}

/// <summary>状態・削除の計画・ジョブのコピーと、アプリ名のコピー（docs/design/desktop-settings.md の 2.2）。</summary>
public class CopyTests
{
    [Fact]
    public async Task The_Status_Should_Be_Copied_After_Reading()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        h.Azure.AppDetails.Add(new ContainerAppDetails("demo-tt", "Succeeded", "Running", "img:1", 1.5, "3Gi", "demo-tt--r1", "demo-tt.example.io", FakeAzure.Managed));
        h.Azure.AppDetails.Add(new ContainerAppDetails("demo-db", "Succeeded", "Running", "img|2", 1, "2Gi", null, null, new Dictionary<string, string>()));
        var vm = h.Main.Status;
        vm.CopyCommand.CanExecute(null).Should().BeFalse();
        vm.ToMarkdown().Should().BeNull();

        await vm.RefreshCommand.ExecuteAsync();
        await vm.CopyCommand.ExecuteAsync();

        var markdown = h.Clipboard.Copied.Single();
        markdown.Should().StartWith("# TTDeployTool の状態の確認（rg / demo）\n\n- サブスクリプション: sub\n- Container Apps Environment demo-env（japaneast、Succeeded）\n- https://demo-tt.example.io/")
            .And.Contain("## Container Apps\n\n| 役割 | 名前 | 状態 | イメージ | CPU / メモリ | リビジョン | 管理タグ |")
            .And.Contain("| Timetracker | demo-tt | Succeeded / Running | img:1 | 1.5 vCPU / 3Gi | demo-tt--r1 | あり |")
            .And.Contain("| DB | demo-db | Succeeded / Running | img\\|2 | 1 vCPU / 2Gi |  | なし |");
        vm.Message.Should().Be("状態を Markdown でコピーしました。");

        h.Main.Target.AppName = "other";
        vm.CopyCommand.CanExecute(null).Should().BeFalse();
        vm.Message.Should().BeNull();
    }

    [Fact]
    public async Task An_Empty_Status_Should_Be_Copied_Without_The_Table()
    {
        var h = await new Harness().ReadyAsync();

        await h.Main.Status.RefreshCommand.ExecuteAsync();

        h.Main.Status.ToMarkdown().Should().Contain("- デプロイされた環境はありません。").And.NotContain("## Container Apps");
    }

    [Fact]
    public async Task The_Deletion_Plan_And_The_App_Name_Should_Be_Copied()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        var vm = h.Main.Deletion;
        vm.CopyPlanCommand.CanExecute(null).Should().BeFalse();
        vm.CopyAppNameCommand.CanExecute(null).Should().BeFalse();

        await vm.PlanCommand.ExecuteAsync();
        await vm.CopyAppNameCommand.ExecuteAsync();
        vm.Message.Should().Be("アプリ名 demo をコピーしました。");
        await vm.CopyPlanCommand.ExecuteAsync();

        h.Clipboard.Copied[0].Should().Be("demo");
        h.Clipboard.Copied[1].Should().StartWith("# TTDeployTool の削除の計画（rg / demo）\n\n| 操作 | 名前 | 種類 | 理由 |")
            .And.Contain("| 削除する | demo-tt | Container App |  |")
            .And.Contain("## 警告\n\n- " + DeletionViewModel.DbDataLossWarning)
            .And.Contain("## 残すもの\n\nリソースグループ rg は削除しません")
            .And.NotContain("## 実行できない理由");
        vm.Message.Should().Be("削除の計画を Markdown でコピーしました。");
    }

    [Fact]
    public async Task A_Plan_With_Nothing_To_Delete_Should_Copy_The_Note()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Deletion;

        await vm.PlanCommand.ExecuteAsync();

        vm.CopyAppNameCommand.CanExecute(null).Should().BeFalse("確認するアプリ名が無い");
        vm.ToMarkdown().Should().Contain("## 注意\n\n削除するリソースはありません。");
    }

    [Fact]
    public async Task A_Blocked_Plan_Should_Copy_The_Blockers()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        h.Azure.Permissions[0] = new TTDeployTool.Domain.Azure.PermissionSet(["*/read"], []);

        await h.Main.Deletion.PlanCommand.ExecuteAsync();

        h.Main.Deletion.ToMarkdown().Should().Contain("## 実行できない理由\n\n- 権限が不足しています");
    }

    [Fact]
    public async Task A_Job_Should_Be_Copied_With_Its_Result()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();
        var job = h.Main.CurrentJob!;

        await job.CopyCommand.ExecuteAsync();

        h.Clipboard.Copied.Single().Should().StartWith($"# TTDeployTool のジョブ: デプロイ（rg / demo）\n\n- 状態: 完了\n- {job.TimesText}\n- 3 / 3: ")
            .And.Contain("- https://demo-tt.example.io/")
            .And.Contain("- 利用可能期限: ")
            .And.NotContain("## エラー");
        job.CopyMessage.Should().Be("ジョブの内容を Markdown でコピーしました。");
    }

    [Fact]
    public async Task A_Deletion_Job_Should_Be_Copied_With_Its_Logs()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.Deployed();
        await h.Main.Deletion.PlanCommand.ExecuteAsync();
        h.Main.Deletion.ConfirmAppName = "demo";
        await h.Main.Deletion.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Main.CurrentJob!.ToMarkdown().Should().Contain("- 削除したもの: demo-tt").And.Contain("## ログ\n\n```\n").And.Contain("削除中: demo-tt");
    }

    [Fact]
    public async Task A_Failed_Job_Should_Be_Copied_With_The_Error()
    {
        var h = await new Harness().ReadyAsync();
        h.Azure.ResourceGroup = null;
        h.Azure.GatewayMock.Setup(g => g.EnsureResourceGroupAsync("sub", "rg", "japaneast", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("作れません"));
        await h.Main.Deployment.PlanCommand.ExecuteAsync();
        await h.Main.Deployment.StartCommand.ExecuteAsync();
        await h.Main.Queue.WhenIdleAsync();

        h.Main.CurrentJob!.ToMarkdown().Should().Contain("## エラー\n\n");
    }

    [Fact]
    public void A_Job_Outside_The_Queue_Cannot_Be_Copied()
    {
        var jobs = new JobManager(new InMemoryJobStore(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<JobManager>.Instance);
        var job = new JobViewModel(jobs.TryStart(JobKind.Deploy, new JobTarget("sub", "rg", "demo"), "local", _ => Task.FromResult<object?>(null)).Job!);

        job.CopyCommand.CanExecute(null).Should().BeFalse();
        job.ToMarkdown().Should().StartWith("# TTDeployTool のジョブ: デプロイ（rg / demo）").And.NotContain("## ログ");
    }
}
