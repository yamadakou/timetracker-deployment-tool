using System.CommandLine;
using System.CommandLine.Invocation;
using Timetracker.Common;
using Timetracker.Domain.Deployment;
using System.IO;

var root = new RootCommand("Timetracker deploy CLI (Container Apps, スペック/タグ/Verbose対応)");

// Define options as variables
var subscriptionOption = new Option<string>("--subscription", "Azure サブスクリプション ID") { IsRequired = true };
var resourceGroupOption = new Option<string>("--resource-group", "Azure リソースグループ名") { IsRequired = true };
var locationOption = new Option<string>("--location", () => Defaults.DefaultLocation, "Azure リージョン（省略時は japaneast）");
var appNameOption = new Option<string>("--app-name", () => Defaults.DefaultAppName, "アプリ名（省略時は TimeTracker）");
var dbTypeOption = new Option<string>("--db-type", () => "postgres", "DB 種別 postgres | sqlserver");
var dbPasswordOption = new Option<string>("--db-password", "DB パスワード（DB 種別にかかわらず同一パラメータ）") { IsRequired = true };
var dbNameOption = new Option<string>("--db-name", () => "timetracker", "DB 名");
var trackerPasswordOption = new Option<string>("--tracker-password", "Timetracker アプリ用パスワード (<your password>)") { IsRequired = true };
var ttTagOption = new Option<string>("--tt-tag", () => "latest", "timetracker コンテナイメージのタグ（バージョン）。例: latest, 1.2.3");
var dryRunOption = new Option<bool>("--dry-run", () => false, "ファイル生成のみ（Compose/.env）で Azure 実行をスキップ");
var ttCpuOption = new Option<double>("--tt-cpu", () => 0.5, "Timetracker コンテナの vCPU");
var ttMemoryOption = new Option<double>("--tt-memory", () => 1.0, "Timetracker コンテナのメモリ(Gi)");
var dbCpuOption = new Option<double>("--db-cpu", () => 0.5, "DB コンテナの vCPU");
var dbMemoryOption = new Option<double>("--db-memory", () => 1.0, "DB コンテナのメモリ(Gi)");
var redisCpuOption = new Option<double>("--redis-cpu", () => 0.25, "Redis コンテナの vCPU");
var redisMemoryOption = new Option<double>("--redis-memory", () => 0.5, "Redis コンテナのメモリ(Gi)");
var verboseOption = new Option<bool>("--verbose", () => false, "詳細ログを出力");

var deploy = new Command("deploy", "Timetracker を Azure Container Apps にデプロイ（または Dry-run で Compose/.env 生成）します。")
{
    subscriptionOption,
    resourceGroupOption,
    locationOption,
    appNameOption,
    dbTypeOption,
    dbPasswordOption,
    dbNameOption,
    trackerPasswordOption,
    ttTagOption,
    dryRunOption,
    ttCpuOption,
    ttMemoryOption,
    dbCpuOption,
    dbMemoryOption,
    redisCpuOption,
    redisMemoryOption,
    verboseOption
};

deploy.SetHandler(async (InvocationContext ctx) =>
{
    var p = ctx.ParseResult;
    var opts = new DeployOptions
    {
        Subscription        = p.GetValueForOption(subscriptionOption)!,
        ResourceGroup       = p.GetValueForOption(resourceGroupOption)!,
        Location            = p.GetValueForOption(locationOption)!,
        AppName             = p.GetValueForOption(appNameOption)!,
        DbType              = p.GetValueForOption(dbTypeOption)!,
        DbPassword          = p.GetValueForOption(dbPasswordOption)!,
        DbName              = p.GetValueForOption(dbNameOption)!,
        TrackerPassword     = p.GetValueForOption(trackerPasswordOption)!,
        TimetrackerTag      = p.GetValueForOption(ttTagOption)!,
        DryRun              = p.GetValueForOption(dryRunOption),
        TimetrackerCpu      = p.GetValueForOption(ttCpuOption),
        TimetrackerMemoryGi = p.GetValueForOption(ttMemoryOption),
        DbCpu               = p.GetValueForOption(dbCpuOption),
        DbMemoryGi          = p.GetValueForOption(dbMemoryOption),
        RedisCpu            = p.GetValueForOption(redisCpuOption),
        RedisMemoryGi       = p.GetValueForOption(redisMemoryOption),
        Verbose             = p.GetValueForOption(verboseOption)
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
            return;
        }

        var sdk = new AzureSdkExecutor(log);
        await sdk.EnsureResourceGroupAsync(opts.Subscription, opts.ResourceGroup, opts.Location);
        var env = await sdk.EnsureContainerAppEnvAsync(opts.Subscription, opts.ResourceGroup, $"{opts.AppName}-env", opts.Location);
        await sdk.CreateOrUpdateContainerAppAsync(opts.Subscription, opts.ResourceGroup, opts.AppName, opts.Location, opts, env);

        log.Success("Container Apps デプロイ完了");
    }
    catch (Exception ex)
    {
        log.Error($"エラー: {ex}");
        Environment.ExitCode = 1;
    }
});

root.AddCommand(deploy);
return await root.InvokeAsync(args);