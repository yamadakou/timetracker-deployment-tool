using System.CommandLine;
using System.CommandLine.Invocation;
using Timetracker.Common;
using Timetracker.Domain.Deployment;
using System.IO;

var root = new RootCommand("Timetracker deploy CLI (Container Apps, スペック/タグ/Verbose対応)");

var subscriptionOpt = new Option<string>("--subscription"){ IsRequired = true, Description = "Azure サブスクリプション ID" };
var resourceGroupOpt = new Option<string>("--resource-group"){ IsRequired = true, Description = "Azure リソースグループ名" };
var locationOpt = new Option<string>("--location", () => Defaults.DefaultLocation, "Azure リージョン（省略時は japaneast）");
var appNameOpt = new Option<string>("--app-name", () => Defaults.DefaultAppName, "アプリ名（省略時は TimeTracker）");
var dbTypeOpt = new Option<string>("--db-type", () => "postgres", "DB 種別 postgres | sqlserver");
var dbPasswordOpt = new Option<string>("--db-password"){ IsRequired = true, Description = "DB パスワード（DB 種別にかかわらず同一パラメータ）" };
var dbNameOpt = new Option<string>("--db-name", () => "timetracker", "DB 名");
var trackerPasswordOpt = new Option<string>("--tracker-password"){ IsRequired = true, Description = "Timetracker アプリ用パスワード (<your password>)" };
var ttTagOpt = new Option<string>("--tt-tag", () => "latest", "timetracker コンテナイメージのタグ（バージョン）。例: latest, 1.2.3");
var dryRunOpt = new Option<bool>("--dry-run", () => false, "ファイル生成のみ（Compose/.env）で Azure 実行をスキップ");
var ttCpuOpt = new Option<double>("--tt-cpu", () => 0.5, "Timetracker コンテナの vCPU");
var ttMemoryOpt = new Option<double>("--tt-memory", () => 1.0, "Timetracker コンテナのメモリ(Gi)");
var dbCpuOpt = new Option<double>("--db-cpu", () => 0.5, "DB コンテナの vCPU");
var dbMemoryOpt = new Option<double>("--db-memory", () => 1.0, "DB コンテナのメモリ(Gi)");
var redisCpuOpt = new Option<double>("--redis-cpu", () => 0.25, "Redis コンテナの vCPU");
var redisMemoryOpt = new Option<double>("--redis-memory", () => 0.5, "Redis コンテナのメモリ(Gi)");
var verboseOpt = new Option<bool>("--verbose", () => false, "詳細ログを出力");

var deploy = new Command("deploy", "Timetracker を Azure Container Apps にデプロイ（または Dry-run で Compose/.env 生成）します。");
deploy.AddOption(subscriptionOpt);
deploy.AddOption(resourceGroupOpt);
deploy.AddOption(locationOpt);
deploy.AddOption(appNameOpt);
deploy.AddOption(dbTypeOpt);
deploy.AddOption(dbPasswordOpt);
deploy.AddOption(dbNameOpt);
deploy.AddOption(trackerPasswordOpt);
deploy.AddOption(ttTagOpt);
deploy.AddOption(dryRunOpt);
deploy.AddOption(ttCpuOpt);
deploy.AddOption(ttMemoryOpt);
deploy.AddOption(dbCpuOpt);
deploy.AddOption(dbMemoryOpt);
deploy.AddOption(redisCpuOpt);
deploy.AddOption(redisMemoryOpt);
deploy.AddOption(verboseOpt);

deploy.SetHandler(async (InvocationContext ctx) =>
{
    var p = ctx.ParseResult;
    var opts = new DeployOptions
    {
        Subscription        = p.GetValueForOption(subscriptionOpt)!,
        ResourceGroup       = p.GetValueForOption(resourceGroupOpt)!,
        Location            = p.GetValueForOption(locationOpt)!,
        AppName             = p.GetValueForOption(appNameOpt)!,
        DbType              = p.GetValueForOption(dbTypeOpt)!,
        DbPassword          = p.GetValueForOption(dbPasswordOpt)!,
        DbName              = p.GetValueForOption(dbNameOpt)!,
        TrackerPassword     = p.GetValueForOption(trackerPasswordOpt)!,
        TimetrackerTag      = p.GetValueForOption(ttTagOpt)!,
        DryRun              = p.GetValueForOption(dryRunOpt),
        TimetrackerCpu      = p.GetValueForOption(ttCpuOpt),
        TimetrackerMemoryGi = p.GetValueForOption(ttMemoryOpt),
        DbCpu               = p.GetValueForOption(dbCpuOpt),
        DbMemoryGi          = p.GetValueForOption(dbMemoryOpt),
        RedisCpu            = p.GetValueForOption(redisCpuOpt),
        RedisMemoryGi       = p.GetValueForOption(redisMemoryOpt),
        Verbose             = p.GetValueForOption(verboseOpt)
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
        log.Error($"エラー: {ex.Message}");
        Environment.ExitCode = 1;
    }
});

root.AddCommand(deploy);
return await root.InvokeAsync(args);