using Timetracker.Common;
using Timetracker.Controller.Cli;
using Timetracker.Domain.Deployment;
using System.IO;

// シンプルな手動パーサで System.CommandLine API 依存を排除 (CS1061 回避)
var argsDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var boolFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--dry-run", "--verbose" };
for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (!a.StartsWith("--")) continue;
    if (boolFlags.Contains(a))
    {
        argsDict[a] = "true"; // フラグ形式
        continue;
    }
    // 次が値か判定
    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
    {
        argsDict[a] = args[i + 1];
        i++; // 値消費
    }
    else
    {
        // 値未指定 → 空文字
        argsDict[a] = string.Empty;
    }
}

string GetOpt(string name, string defaultValue) =>
    argsDict.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : defaultValue;
bool GetBool(string name) => argsDict.TryGetValue(name, out var v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
double GetDouble(string name, double def)
    => argsDict.TryGetValue(name, out var v) && double.TryParse(v, out var d) ? d : def;

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
    Console.Error.WriteLine("[ERROR] 必須パラメータ不足: --subscription --resource-group --db-password --tracker-password を指定してください。");
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
var ttCpu           = GetDouble("--tt-cpu", 0.5);
var ttMem           = GetDouble("--tt-memory", 1.0);
var dbCpu           = GetDouble("--db-cpu", 0.5);
var dbMem           = GetDouble("--db-memory", 1.0);
var redisCpu        = GetDouble("--redis-cpu", 0.25);
var redisMem        = GetDouble("--redis-memory", 0.5);

var appNameError = AppNameValidator.GetError(normalizedAppName);
if (appNameError != null)
{
    Console.Error.WriteLine($"[ERROR] アプリ名 '{rawAppName}' は無効です: {appNameError}");
    Console.Error.WriteLine("  - 英小文字 (a-z)、数字 (0-9)、ハイフン (-) のみ使用可能");
    Console.Error.WriteLine("  - 英小文字で始まる/終わる必要があります（終端は英小文字または数字）");
    Console.Error.WriteLine("  - 連続するハイフン '--' は不可 / 長さ 2〜32 文字");
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