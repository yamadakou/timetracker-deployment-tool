using System.Diagnostics;
using FluentAssertions;
using Microsoft.Playwright;
using Timetracker.Controller.Cli;
using Xunit;

namespace Timetracker.SystemTests.E2E;

/// <summary>
/// CLI の Dry-run で生成した docker-compose.yml でローカルに起動し、セットアップウィザードでサンプルデータベースを作って、
/// サンプルデータベースのユーザーでログインできることを確かめる（PostgreSQL と SQL Server）。
/// E2E の設定（E2ESettings）が無い場合と、Docker（docker compose）が使えない場合はスキップする。ポート 8080 を使うため、1 件ずつ実行する。
/// </summary>
public sealed class LocalComposeSetupTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromMinutes(30);

    [Theory(Timeout = 50 * 60 * 1000)]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public async Task Sample_Database_Setup_And_Login_Should_Succeed(string dbType)
    {
        var (licenseKey, user) = E2ESettings.Require();
        Assert.SkipUnless(await DockerComposeAvailableAsync(), "docker compose が使えないためスキップします。");
        var ct = TestContext.Current.CancellationToken;

        var dir = Directory.CreateTempSubdirectory("ttst-e2e-").FullName;
        var project = $"ttst-e2e-{dbType}";
        var password = AzureTestEnvironment.NewPassword();
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(
            ["deploy", "--db-type", dbType, "--db-password", password, "--tracker-password", password, "--dry-run", "true"], output, error, dir);
        code.Should().Be(0, $"{output}\n{error}");

        try
        {
            (await RunAsync("docker", ["compose", "-p", project, "up", "-d"], dir)).Should().Be(0);
            var status = await AzureTestEnvironment.WaitForHttpResponseAsync("http://localhost:8080/", StartupTimeout, ct);
            TestContext.Current.TestOutputHelper?.WriteLine($"http://localhost:8080/ → {status}");

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await TimetrackerSetupWizard.LaunchBrowserAsync(playwright);
            var page = await browser.NewPageAsync(new() { Locale = "ja-JP" });
            var wizard = new TimetrackerSetupWizard(page);

            await wizard.CreateSampleDatabaseAsync("http://localhost:8080", licenseKey, SetupTimeout);
            var home = await wizard.LoginFromCompletionAsync(user);

            home.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await RunAsync("docker", ["compose", "-p", project, "down", "-v"], dir);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<bool> DockerComposeAvailableAsync()
    {
        try
        {
            return await RunAsync("docker", ["compose", "version"], Directory.GetCurrentDirectory(), quiet: true) == 0
                && await RunAsync("docker", ["info"], Directory.GetCurrentDirectory(), quiet: true) == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // docker コマンドが無い
        }
    }

    private static async Task<int> RunAsync(string command, string[] args, string workingDirectory, bool quiet = false)
    {
        var start = new ProcessStartInfo(command, args)
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!quiet)
            TestContext.Current.TestOutputHelper?.WriteLine($"$ {command} {string.Join(" ", args)} → {process.ExitCode}\n{await stdout}{await stderr}");
        return process.ExitCode;
    }
}
