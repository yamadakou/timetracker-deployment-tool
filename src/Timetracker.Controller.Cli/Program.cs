using Timetracker.Common;
using Timetracker.Controller.Cli;
using Timetracker.Domain.Deployment;

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

string GetOpt(string name, string defaultValue) => cli.Get(name, defaultValue);
bool GetBool(string name) => cli.GetBool(name);
double GetDouble(string name, double def) => cli.GetDouble(name, def);

// 必須オプション取得 (subscription / resource-group / db-password / tracker-password)
var subscription    = GetOpt("--subscription",  string.Empty);
var resourceGroup   = GetOpt("--resource-group", string.Empty);
var dbPassword      = GetOpt("--db-password",   string.Empty);
var trackerPassword = GetOpt("--tracker-password", string.Empty);

if (string.IsNullOrWhiteSpace(subscription) ||
    string.IsNullOrWhiteSpace(resourceGroup) ||
    string.IsNullOrWhiteSpace(dbPassword) ||
    string.IsNullOrWhiteSpace(trackerPassword))
{
    Console.Error.WriteLine("[ERROR] 必須パラメータ不足: --subscription --resource-group --db-password --tracker-password を指定してください。（--help で使い方を表示）");
    return 1;
}

var location        = GetOpt("--location", Defaults.DefaultLocation);
var rawAppName      = GetOpt("--app-name", Defaults.DefaultAppName);
var normalizedAppName = rawAppName.ToLowerInvariant();
var dbType          = GetOpt("--db-type", "postgresql");
var dbName          = GetOpt("--db-name", "timetracker");
var ttTag           = GetOpt("--tt-tag", "7.0-linux-postgres");
var dryRun          = GetBool("--dry-run");
var verbose         = GetBool("--verbose");
var authMode        = GetOpt("--auth-mode", "default");

// デフォルト CPU/メモリは要件値に合わせる（未指定時のみ適用）
var ttCpu           = GetDouble("--tt-cpu", 0.75);
var ttMem           = GetDouble("--tt-memory", 1.5);
var dbCpu           = GetDouble("--db-cpu", 1.0);
var dbMem           = GetDouble("--db-memory", 2.0);
var redisCpu        = GetDouble("--redis-cpu", 0.5);
var redisMem        = GetDouble("--redis-memory", 1.0);

var appNameError = AppNameValidator.GetError(normalizedAppName);
if (appNameError != null)
{
    Console.Error.WriteLine($"[ERROR] アプリ名 '{rawAppName}' は無効です: {appNameError}");
    Console.Error.WriteLine("  - 英小文字 (a-z)、数字 (0-9)、ハイフン (-) のみ使用可能");
    Console.Error.WriteLine("  - 英小文字で始まる/終わる必要があります（終端は英小文字または数字）");
    Console.Error.WriteLine($"  - 連続するハイフン '--' は不可 / 長さ {AppNameValidator.MinLength}〜{AppNameValidator.MaxLength} 文字");
    return 1;
}

var opts = new DeployOptions
{
    Subscription        = subscription,
    ResourceGroup       = resourceGroup,
    Location            = location,
    AppName             = normalizedAppName,
    DbType              = dbType,
    DbPassword          = dbPassword,
    DbName              = dbName,
    TrackerPassword     = trackerPassword,
    TimetrackerTag      = ttTag,
    DryRun              = dryRun,
    TimetrackerCpu      = ttCpu,
    TimetrackerMemoryGi = ttMem,
    DbCpu               = dbCpu,
    DbMemoryGi          = dbMem,
    RedisCpu            = redisCpu,
    RedisMemoryGi       = redisMem,
    Verbose             = verbose,
    AuthMode            = authMode
};

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
    var sdk = new AzureSdkExecutor(log, credential);
    await sdk.EnsureResourceGroupAsync(opts.Subscription, opts.ResourceGroup, opts.Location);
    var env = await sdk.EnsureContainerAppEnvAsync(opts.Subscription, opts.ResourceGroup, $"{opts.AppName}-env", opts.Location);
    await sdk.CreateOrUpdateContainerAppAsync(opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, opts, env);
    log.Success("Container Apps デプロイ完了");
    return 0;
}
catch (Exception ex)
{
    log.Error($"エラー: {ex.Message}");
    return 1;
}