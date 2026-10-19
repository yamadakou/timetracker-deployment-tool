using System.Diagnostics.Tracing;
using Azure;
using Azure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using TTDeployTool.Controller.Desktop.Core.Diagnostics;
using Xunit;

namespace TTDeployTool.Controller.Desktop.Core.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ttdeploytool-desktop-tests", Guid.NewGuid().ToString("N"));
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DiagnosticLog Create() => new(Path.Combine(_directory, "logs"), _time);

    private static string Read(DiagnosticLog log) => File.ReadAllText(log.CurrentFile);

    [Fact]
    public void Logs_Should_Be_Written_To_A_File_Per_Day_From_Information()
    {
        using var log = Create();
        var logger = log.CreateLogger("Test");

        log.Directory.Should().Be(Path.Combine(_directory, "logs"));
        Path.GetFileName(log.CurrentFile).Should().Be($"ttdeploytool-desktop-{_time.GetLocalNow():yyyyMMdd}.log");
        logger.LogDebug("詳細");
        logger.LogInformation("情報 {Value}", 1);
        logger.LogError(new InvalidOperationException("壊れた"), "失敗");

        var text = Read(log);
        text.Should().NotContain("詳細");
        text.Should().Contain("[Information] Test: 情報 1").And.Contain("[Error] Test: 失敗").And.Contain("System.InvalidOperationException: 壊れた");
        logger.IsEnabled(LogLevel.None).Should().BeFalse();
        logger.BeginScope("範囲").Should().BeNull();
        log.Invoking(l => l.AddProvider(null!)).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Failing_To_Write_Should_Not_Stop_The_App()
    {
        using var log = Create();
        // 今日のファイルの場所にフォルダーがあると書けない
        Directory.CreateDirectory(log.CurrentFile);

        log.CreateLogger("Test").Invoking(l => l.LogWarning("書けない")).Should().NotThrow();
    }

    [Fact]
    public void Old_Files_Should_Be_Deleted_And_Locked_Ones_Kept()
    {
        using var log = Create();
        string Touch(string name, TimeSpan age)
        {
            var path = Path.Combine(log.Directory, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime - age);
            return path;
        }
        var old = Touch("ttdeploytool-desktop-20261001.log", TimeSpan.FromDays(8));
        var recent = Touch("ttdeploytool-desktop-20261018.log", TimeSpan.FromDays(1));
        var other = Touch("other.log", TimeSpan.FromDays(30));
        var locked = Touch("ttdeploytool-desktop-20261002.log", TimeSpan.FromDays(9));
        log.DeleteFile = path =>
        {
            if (path == locked)
                throw new IOException("使用中");
            File.Delete(path);
        };

        log.DeleteOld();

        File.Exists(old).Should().BeFalse();
        File.Exists(recent).Should().BeTrue();
        File.Exists(other).Should().BeTrue();
        File.Exists(locked).Should().BeTrue();
    }

    [Theory]
    [InlineData(EventLevel.Critical, LogLevel.Critical)]
    [InlineData(EventLevel.Error, LogLevel.Error)]
    [InlineData(EventLevel.Warning, LogLevel.Warning)]
    [InlineData(EventLevel.Informational, LogLevel.Information)]
    [InlineData(EventLevel.LogAlways, LogLevel.Information)]
    [InlineData(EventLevel.Verbose, LogLevel.Debug)]
    public void Azure_Event_Levels_Should_Map_To_Log_Levels(EventLevel level, LogLevel expected) =>
        DiagnosticLog.ToLogLevel(level).Should().Be(expected);

    [Fact]
    public void Azure_Identity_Information_And_Other_Warnings_Should_Be_Forwarded()
    {
        using var log = Create();
        var logger = log.CreateLogger("Azure");

        DiagnosticLog.Forward(logger, "Azure-Identity", EventLevel.Informational, "GetToken を呼びました");
        DiagnosticLog.Forward(logger, "Azure-Core", EventLevel.Informational, "要求を送りました");
        DiagnosticLog.Forward(logger, "Azure-Core", EventLevel.Warning, "応答 401");

        var text = Read(log);
        text.Should().Contain("[Information] Azure: Azure-Identity: GetToken を呼びました").And.Contain("[Warning] Azure: Azure-Core: 応答 401");
        text.Should().NotContain("要求を送りました");
    }

    [Fact]
    public void Azure_Events_Should_Be_Written_While_Forwarding()
    {
        using var log = Create();

        using (log.ForwardAzureEvents())
        {
            TestAzureEvents.Log.Failed("テストの警告");
            TestAzureEvents.Log.Sent("テストの情報");
        }
        TestAzureEvents.Log.Failed("止めた後");

        var text = Read(log);
        text.Should().Contain("Azure-DesktopDiagnosticsTest: テストの警告");
        text.Should().NotContain("テストの情報").And.NotContain("止めた後");
    }

    [Fact]
    public async Task Sign_In_And_Loading_Should_Be_Logged_Without_The_Account_Name()
    {
        using var log = Create();
        var h = new Harness(log);
        h.Azure.TenantList.Add(new("t2", "MFA が必要", null));
        h.Azure.TenantErrors["t2"] = new AuthenticationFailedException("AADSTS50076: mfa");

        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        h.Azure.TenantsError = new RequestFailedException(401, "だめ");
        await h.Main.Target.LoadCommand.ExecuteAsync();
        h.SignIn.SignOutError = new InvalidOperationException("消せません");
        await h.Main.SignIn.SignOutCommand.ExecuteAsync();
        h.SignIn.SignInError = new OperationCanceledException();
        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        h.SignIn.SignInError = new InvalidOperationException("WAM が使えません");
        await h.Main.SignIn.SignInCommand.ExecuteAsync();
        h.SignIn.RestoreError = new InvalidOperationException("壊れた記録");
        await h.Main.InitializeAsync();

        var text = Read(log);
        text.Should().Contain("サインインの状態: サインイン（ホームのテナント home）")
            .And.Contain("テナント 2 件")
            .And.Contain("テナント t1: サブスクリプション 1 件")
            .And.Contain("[Warning] TTDeployTool.Desktop.Target: テナント t2 のサブスクリプションを読めませんでした")
            .And.Contain("AADSTS50076")
            .And.Contain("[Error] TTDeployTool.Desktop.Target: テナントの一覧を読めませんでした")
            .And.Contain("[Error] TTDeployTool.Desktop.SignIn: サインアウトに失敗しました")
            .And.Contain("サインインの状態: サインアウト（ホームのテナント (null)）")
            .And.Contain("サインインが取り消されました")
            .And.Contain("[Error] TTDeployTool.Desktop.SignIn: サインインに失敗しました")
            .And.Contain("[Error] TTDeployTool.Desktop.SignIn: 前回のサインインを復元できませんでした");
        text.Should().NotContain(FakeSignIn.Alice.UserName);
    }

    [Fact]
    public async Task The_Log_Folder_Should_Be_Opened_Only_When_Logs_Are_Written_To_Files()
    {
        using var log = Create();
        var withFiles = new Harness(log);
        withFiles.Main.LogDirectory.Should().Be(log.Directory);
        await withFiles.Main.OpenLogFolderCommand.ExecuteAsync();
        withFiles.Launcher.Opened.Should().Equal(log.Directory);

        var withoutFiles = new Harness();
        withoutFiles.Main.LogDirectory.Should().BeNull();
        withoutFiles.Main.OpenLogFolderCommand.CanExecute(null).Should().BeFalse();
    }
}

/// <summary>決まった時刻を返す。</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Azure SDK と同じ印（trait の AzureEventSource）を付けたイベント（AzureEventSourceListener が拾う）。</summary>
[EventSource(Name = "Azure-DesktopDiagnosticsTest")]
internal sealed class TestAzureEvents : EventSource
{
    public static readonly TestAzureEvents Log = new();

    private TestAzureEvents()
        : base(EventSourceSettings.Default, "AzureEventSource", "true")
    {
    }

    [Event(1, Level = EventLevel.Warning, Message = "{0}")]
    public void Failed(string message) => WriteEvent(1, message);

    [Event(2, Level = EventLevel.Informational, Message = "{0}")]
    public void Sent(string message) => WriteEvent(2, message);
}
