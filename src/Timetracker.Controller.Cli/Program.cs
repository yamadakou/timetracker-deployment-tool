using Timetracker.Application.Deployment;
using Timetracker.Common;
using Timetracker.Controller.Cli;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

CliArgs cli;
try
{
    cli = CliArgs.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    return 1;
}

if (cli.IsHelp)
{
    Console.WriteLine(CliArgs.HelpText);
    return 0;
}

// 必須オプション (subscription / resource-group / db-password / tracker-password)
if (string.IsNullOrWhiteSpace(cli.Get("--subscription")) ||
    string.IsNullOrWhiteSpace(cli.Get("--resource-group")) ||
    string.IsNullOrWhiteSpace(cli.Get("--db-password")) ||
    string.IsNullOrWhiteSpace(cli.Get("--tracker-password")))
{
    Console.Error.WriteLine("[ERROR] 必須パラメータ不足: --subscription --resource-group --db-password --tracker-password を指定してください。（--help で使い方を表示）");
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
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    return 1;
}

var opts = DeployOptionsResolver.Resolve(input);

var appNameError = AppNameValidator.GetError(opts.AppName);
if (appNameError != null)
{
    var rawAppName = input.AppName ?? Defaults.DefaultAppName;
    Console.Error.WriteLine($"[ERROR] アプリ名 '{rawAppName}' は無効です: {appNameError}");
    Console.Error.WriteLine("  - 英小文字 (a-z)、数字 (0-9)、ハイフン (-) のみ使用可能");
    Console.Error.WriteLine("  - 英小文字で始まる/終わる必要があります（終端は英小文字または数字）");
    Console.Error.WriteLine($"  - 連続するハイフン '--' は不可 / 長さ {AppNameValidator.MinLength}〜{AppNameValidator.MaxLength} 文字");
    return 1;
}

var log = new SimpleLogger(opts.Verbose);
try
{
    ComposeGenerator.Validate(opts);
    log.Info("検証 OK");

    if (opts.DryRun)
    {
        var compose = ComposeGenerator.GenerateCompose(opts);
        await File.WriteAllTextAsync(opts.OutputCompose, compose);
        log.Info($"Compose 生成: {opts.OutputCompose}");
        var envContent = ComposeGenerator.GenerateEnv(opts);
        await File.WriteAllTextAsync(".env", envContent);
        log.Info(".env 生成: .env");
        log.Warn("Dry-run のため Container Apps デプロイは行いません。");
        return 0;
    }

    var credential = CredentialFactory.Create(opts.AuthMode, log);
    var runner = new DeploymentRunner(new AzureSdkExecutor(log, credential));
    await runner.RunAsync(opts);
    log.Success("Container Apps デプロイ完了");
    return 0;
}
catch (Exception ex)
{
    log.Exception(ex);
    return 1;
}