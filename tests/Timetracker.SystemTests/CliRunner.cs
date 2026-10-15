using Azure.Identity;
using Timetracker.Controller.Cli;
using Timetracker.Infrastructure.Azure;
using Xunit;

namespace Timetracker.SystemTests;

/// <summary>CLI の 1 回の実行結果。</summary>
public sealed record CliResult(int ExitCode, string Output, string Error)
{
    public override string ToString() => $"exit={ExitCode}\n--- stdout\n{Output}\n--- stderr\n{Error}";
}

/// <summary>
/// CLI（CliApplication）を本物の Azure に対して実行する。Azure の操作は差し替えない。
/// 出力はテストの出力（TestContext）にも書き出し、失敗時に追えるようにする。
/// </summary>
public sealed class CliRunner
{
    private readonly string _subscriptionId;
    private readonly string _resourceGroup;
    private readonly CliApplication.GatewayFactory? _gatewayFactory;

    /// <param name="gatewayFactory">省略時は --auth-mode azure-cli（実行環境の az login）で操作する</param>
    public CliRunner(string subscriptionId, string resourceGroup, CliApplication.GatewayFactory? gatewayFactory = null)
    {
        _subscriptionId = subscriptionId;
        _resourceGroup = resourceGroup;
        _gatewayFactory = gatewayFactory;
    }

    /// <summary>指定したサービスプリンシパルの資格情報で Azure を操作する。</summary>
    public static CliApplication.GatewayFactory ServicePrincipal(string tenantId, string clientId, string clientSecret) =>
        (_, logger) => new AzureSdkExecutor(logger, new ClientSecretCredential(tenantId, clientId, clientSecret));

    public Task<CliResult> DeployAsync(string appName, string password, params string[] extra) =>
        RunAsync(["deploy", "--subscription", _subscriptionId, "--resource-group", _resourceGroup, "--app-name", appName,
            "--auth-mode", "azure-cli", "--db-password", password, "--tracker-password", password, "--verbose", .. extra]);

    public Task<CliResult> DeleteAsync(string appName, string? input, params string[] extra) =>
        RunAsync(["delete", "--subscription", _subscriptionId, "--resource-group", _resourceGroup, "--app-name", appName,
            "--auth-mode", "azure-cli", "--verbose", .. extra], input);

    private async Task<CliResult> RunAsync(string[] args, string? input = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, output, error, Path.GetTempPath(), _gatewayFactory, new StringReader(input ?? ""));
        var result = new CliResult(code, output.ToString(), error.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine($"$ timetracker-cli {Display(args)}\n{result}");
        return result;
    }

    // パスワードはテストの出力に残さない
    private static string Display(string[] args) =>
        string.Join(" ", args.Select((a, i) => i > 0 && args[i - 1] is "--db-password" or "--tracker-password" ? "***" : a));
}
