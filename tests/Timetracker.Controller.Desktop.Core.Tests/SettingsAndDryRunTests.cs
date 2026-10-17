using FluentAssertions;
using Timetracker.Controller.Desktop.Core.ViewModels;
using Timetracker.Domain.Deployment;
using Xunit;

namespace Timetracker.Controller.Desktop.Core.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void Defaults_Should_Only_Lack_Passwords_And_The_Target()
    {
        var vm = new Harness().Main.Settings;

        vm.TtCpu.Should().Be("1.5");
        vm.TtMemoryGi.Should().Be("3.0");
        vm.DefaultTag.Should().Be(Defaults.GetDefaultTimetrackerTag("postgresql"));
        vm.Errors.Should().Equal("subscription は必須です。", "resource-group は必須です。",
            "tracker-password が不足または短すぎます。", "db-password が不足または短すぎます。");
        vm.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Inputs_Should_Be_Validated_As_They_Change()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Settings;
        vm.Errors.Should().BeEmpty();
        var changes = new ChangeRecorder(vm);

        vm.DbType = "sqlserver";
        vm.DbPassword = "simplepassword";
        vm.TtMemoryGi = "2";
        vm.TtCpu = "1,5";
        vm.RedisCpu = " ";

        changes.Names.Should().Contain("DefaultTag");
        vm.DefaultTag.Should().Be(Defaults.GetDefaultTimetrackerTag("sqlserver"));
        vm.Errors.Should().HaveCount(2);
        vm.Errors[0].Should().Be("tt-cpu には数値を指定してください（小数点は「.」）: '1,5'");
        vm.Errors[1].Should().StartWith("db-password が SQL Server のパスワード要件を満たしていません");
        vm.Warnings.Should().HaveCount(2);
        vm.Build(ValidationTarget.Azure).Options.Should().BeNull();
    }

    [Fact]
    public async Task Build_Should_Resolve_Every_Setting()
    {
        var h = await new Harness().ReadyAsync();
        var vm = h.Main.Settings;
        vm.DbName = " tt ";
        vm.TtTag = " 11.0.1-linux-postgres ";
        vm.TtCpu = "2"; vm.TtMemoryGi = "4"; vm.DbCpu = "1.25"; vm.DbMemoryGi = "2.5"; vm.RedisCpu = "0.75"; vm.RedisMemoryGi = "1.5";

        var o = vm.Build(ValidationTarget.Azure).Options!;

        (o.Subscription, o.ResourceGroup, o.AppName, o.Location, o.DbName, o.TimetrackerTag).Should().Be(("sub", "rg", "demo", "japaneast", "tt", "11.0.1-linux-postgres"));
        (o.TimetrackerCpu, o.TimetrackerMemoryGi, o.DbCpu, o.DbMemoryGi, o.RedisCpu, o.RedisMemoryGi).Should().Be((2, 4, 1.25, 2.5, 0.75, 1.5));
        (o.DbPassword, o.TrackerPassword).Should().Be(("DbPassw0rd!", "TrackerPass!"));
    }

    [Fact]
    public void Compose_Should_Not_Need_The_Azure_Target()
    {
        var vm = new Harness().Main.Settings;
        vm.DbPassword = "DbPassw0rd!";
        vm.TrackerPassword = "TrackerPass!";

        var input = vm.Build(ValidationTarget.Compose);

        input.Errors.Should().BeEmpty();
        input.Options!.Subscription.Should().BeEmpty();
    }

    [Fact]
    public void Setting_The_Same_Value_Should_Not_Notify()
    {
        var vm = new Harness().Main.Settings;
        var changes = new ChangeRecorder(vm);

        vm.DbType = "postgresql";

        changes.Names.Should().BeEmpty();
    }
}

public sealed class DryRunViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tt-dryrun-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Harness Ready()
    {
        var h = new Harness();
        h.Main.Settings.DbPassword = "DbPassw0rd!";
        h.Main.Settings.TrackerPassword = "TrackerPass!";
        return h;
    }

    [Fact]
    public async Task Save_Should_Not_Write_When_Settings_Are_Wrong()
    {
        var h = new Harness();
        h.Dialogs.Folder = _root;

        await h.Main.DryRun.SaveCommand.ExecuteAsync();

        h.Main.DryRun.Error.Should().StartWith("設定に誤りがあるため、ファイルを保存しませんでした:\n- tracker-password");
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task Save_Should_Do_Nothing_When_No_Folder_Is_Chosen()
    {
        var h = Ready();

        await h.Main.DryRun.SaveCommand.ExecuteAsync();

        h.Main.DryRun.SavedDirectory.Should().BeNull();
        h.Main.DryRun.OpenFolderCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Save_Should_Write_The_Files_And_Show_The_Next_Steps()
    {
        var h = Ready();
        h.Dialogs.Folder = _root;
        h.Main.Settings.TtMemoryGi = "2";
        var vm = h.Main.DryRun;

        await vm.SaveCommand.ExecuteAsync();

        vm.Error.Should().BeNull();
        vm.SavedDirectory.Should().Be(_root);
        vm.ComposeContent.Should().Be(File.ReadAllText(Path.Combine(_root, "docker-compose.yml"))).And.NotContain("DbPassw0rd!");
        File.ReadAllText(Path.Combine(_root, ".env")).Should().Contain("DbPassw0rd!");
        vm.EnvWarning.Should().Be(DesktopMessages.EnvFileWarning);
        vm.Warnings.Should().ContainSingle().Which.Should().StartWith("tt-memory が 2Gi です。");
        vm.NextSteps.Should().HaveCount(3).And.Contain($"{_root} で docker compose up -d を実行する");
        h.Dialogs.Confirmations.Should().BeEmpty();

        await vm.OpenFolderCommand.ExecuteAsync();
        h.Launcher.Opened.Should().Equal(_root);
    }

    [Fact]
    public async Task Save_Should_Overwrite_Only_After_Confirmation()
    {
        var h = Ready();
        h.Dialogs.Folder = _root;
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".env"), "old");

        await h.Main.DryRun.SaveCommand.ExecuteAsync();
        File.ReadAllText(Path.Combine(_root, ".env")).Should().Be("old");
        h.Main.DryRun.SavedDirectory.Should().BeNull();

        h.Dialogs.Confirm = true;
        await h.Main.DryRun.SaveCommand.ExecuteAsync();
        File.ReadAllText(Path.Combine(_root, ".env")).Should().Contain("DbPassw0rd!");
        h.Dialogs.Confirmations.Should().Equal("ファイルの上書き", "ファイルの上書き");
    }

    [Fact]
    public async Task Save_Should_Show_Files_That_Cannot_Be_Written()
    {
        var h = Ready();
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "file");
        File.WriteAllText(file, "");
        h.Dialogs.Folder = file;

        await h.Main.DryRun.SaveCommand.ExecuteAsync();

        h.Main.DryRun.Error.Should().StartWith($"{file} に書き込めませんでした: ");
        h.Main.DryRun.SavedDirectory.Should().BeNull();
    }
}
