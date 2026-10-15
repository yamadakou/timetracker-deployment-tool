using Timetracker.Application.Deployment;
using Timetracker.Common;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Cli;

/// <summary>
/// CLI の処理本体（引数の解析 → 検証 → Dry-run またはデプロイ）。
/// テストで出力先・Dry-run の出力ディレクトリ・Azure の操作を差し替えられるよう Program から分離している。
/// </summary>
public static class CliApplication
{
    /// <summary>デプロイで使う Azure の操作を作る。</summary>
    public delegate IAzureDeploymentGateway GatewayFactory(DeployOptions opts, IDeploymentLogger logger);

    /// <summary>--auth-mode に応じた資格情報で Azure SDK を使う。</summary>
    public static readonly GatewayFactory DefaultGatewayFactory =
        (opts, logger) => new AzureSdkExecutor(logger, CredentialFactory.Create(opts.AuthMode, logger));

    /// <param name="args">コマンドライン引数</param>
    /// <param name="outputWriter">標準出力（省略時は Console.Out）</param>
    /// <param name="errorWriter">標準エラー出力（省略時は Console.Error）</param>
    /// <param name="workingDirectory">Dry-run のファイルの出力先（既定の "" はカレントディレクトリ）</param>
    /// <param name="gatewayFactory">Azure の操作（省略時は <see cref="DefaultGatewayFactory"/>）</param>
    /// <returns>終了コード（成功 0 / 失敗 1）</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        string workingDirectory = "",
        GatewayFactory? gatewayFactory = null)
    {
        var output = outputWriter ?? Console.Out;
        var err = errorWriter ?? Console.Error;

        CliArgs cli;
        try
        {
            cli = CliArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            err.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }

        if (cli.IsHelp)
        {
            output.WriteLine(CliArgs.HelpText);
            return 0;
        }

        // 必須オプション。Dry-run は Azure を使わないため subscription / resource-group は不要
        var requiredOptions = cli.GetBool("--dry-run")
            ? new[] { "--db-password", "--tracker-password" }
            : new[] { "--subscription", "--resource-group", "--db-password", "--tracker-password" };
        var missingOptions = requiredOptions.Where(name => cli.Get(name) == null).ToList();
        if (missingOptions.Count > 0)
        {
            err.WriteLine($"[ERROR] 必須パラメータ不足: {string.Join(" ", missingOptions)} を指定してください。（--help で使い方を表示）");
            return 1;
        }

        // 未指定のオプションは null のまま渡し、既定値は DeployOptionsResolver で適用する
        DeployInput input;
        try
        {
            input = new DeployInput
            {
                Subscription        = cli.Get("--subscription"),
                ResourceGroup       = cli.Get("--resource-group"),
                Location            = cli.Get("--location"),
                AppName             = cli.Get("--app-name"),
                DbType              = cli.Get("--db-type"),
                DbName              = cli.Get("--db-name"),
                DbPassword          = cli.Get("--db-password"),
                TrackerPassword     = cli.Get("--tracker-password"),
                TimetrackerTag      = cli.Get("--tt-tag"),
                AuthMode            = cli.Get("--auth-mode"),
                TimetrackerCpu      = cli.GetDouble("--tt-cpu"),
                TimetrackerMemoryGi = cli.GetDouble("--tt-memory"),
                DbCpu               = cli.GetDouble("--db-cpu"),
                DbMemoryGi          = cli.GetDouble("--db-memory"),
                RedisCpu            = cli.GetDouble("--redis-cpu"),
                RedisMemoryGi       = cli.GetDouble("--redis-memory"),
                DryRun              = cli.GetBool("--dry-run"),
                Verbose             = cli.GetBool("--verbose"),
            };
        }
        catch (ArgumentException ex)
        {
            err.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }

        var opts = DeployOptionsResolver.Resolve(input);

        var appNameError = AppNameValidator.GetError(opts.AppName);
        if (appNameError != null)
        {
            var rawAppName = cli.Get("--app-name", Defaults.DefaultAppName);
            err.WriteLine($"[ERROR] アプリ名 '{rawAppName}' は無効です: {appNameError}");
            err.WriteLine("  - 英小文字 (a-z)、数字 (0-9)、ハイフン (-) のみ使用可能");
            err.WriteLine("  - 英小文字で始まる/終わる必要があります（終端は英小文字または数字）");
            err.WriteLine($"  - 連続するハイフン '--' は不可 / 長さ {AppNameValidator.MinLength}〜{AppNameValidator.MaxLength} 文字");
            return 1;
        }

        var log = new SimpleLogger(opts.Verbose, outputWriter, errorWriter);
        try
        {
            if (opts.DryRun)
            {
                ComposeGenerator.ValidateForCompose(opts);
                log.Info("検証 OK");
                var compose = ComposeGenerator.GenerateCompose(opts);
                await File.WriteAllTextAsync(Path.Combine(workingDirectory, opts.OutputCompose), compose);
                log.Info($"Compose 生成: {opts.OutputCompose}");
                var envContent = ComposeGenerator.GenerateEnv(opts);
                await File.WriteAllTextAsync(Path.Combine(workingDirectory, ".env"), envContent);
                log.Info(".env 生成: .env");
                log.Warn("Dry-run のため Container Apps デプロイは行いません。");
                return 0;
            }

            ComposeGenerator.Validate(opts);
            log.Info("検証 OK");
            var runner = new DeploymentRunner((gatewayFactory ?? DefaultGatewayFactory)(opts, log));
            await runner.RunAsync(opts);
            log.Success("Container Apps デプロイ完了");
            return 0;
        }
        catch (Exception ex)
        {
            log.Exception(ex);
            return 1;
        }
    }
}
