using System.CommandLine;
using Timetracker.Common;
using Timetracker.Domain.Deployment;
using System.IO;

var root = new RootCommand("Timetracker deploy CLI (Container Apps, スペック/タグ/Verbose対応)");

var deploy = new Command("deploy", "Timetracker を Azure Container Apps にデプロイ（または Dry-run で Compose/.env 生成）します。")
{
    new Option<string>("--subscription"){ IsRequired = true, Description = "Azure サブスクリプション ID" },
    new Option<string>("--resource-group"){ IsRequired = true, Description = "Azure リソースグループ名" },
    new Option<string>("--location", () => Defaults.DefaultLocation, "Azure リージョン（省略時は japaneast）"),
    new Option<string>("--app-name", () => Defaults.DefaultAppName, "アプリ名（省略時は TimeTracker）"),
    new Option<string>("--db-type", () => "postgres", "DB 種別 postgres | sqlserver"),
    new Option<string>("--db-password"){ IsRequired = true, Description = "DB パスワード（DB 種別にかかわらず同一パラメータ）" },
    new Option<string>("--db-name", () => "timetracker", "DB 名"),
    new Option<string>("--tracker-password"){ IsRequired = true, Description = "Timetracker アプリ用パスワード (<your password>)" },
    new Option<string>("--tt-tag", () => "latest", "timetracker コンテナイメージのタグ（バージョン）。例: latest, 1.2.3"),
    new Option<bool>("--dry-run", () => false, "ファイル生成のみ（Compose/.env）で Azure 実行をスキップ"),
    new Option<double>("--tt-cpu", () => 0.5, "Timetracker コンテナの vCPU"),
    new Option<double>("--tt-memory", () => 1.0, "Timetracker コンテナのメモリ(Gi)"),
    new Option<double>("--db-cpu", () => 0.5, "DB コンテナの vCPU"),
    new Option<double>("--db-memory", () => 1.0, "DB コンテナのメモリ(Gi)"),
    new Option<double>("--redis-cpu", () => 0.25, "Redis コンテナの vCPU"),
    new Option<double>("--redis-memory", () => 0.5, "Redis コンテナのメモリ(Gi)"),
    new Option<bool>("--verbose", () => false, "詳細ログを出力")
};

deploy.SetHandler(async (ctx) =>
{
    var p = ctx.ParseResult;
    var opts = new DeployOptions
    {
        Subscription        = p.GetValueForOption<string>("--subscription")!,
        ResourceGroup       = p.GetValueForOption<string>("--resource-group")!,
        Location            = p.GetValueForOption<string>("--location")!,
        AppName             = p.GetValueForOption<string>("--app-name")!,
        DbType              = p.GetValueForOption<string>("--db-type")!,
        DbPassword          = p.GetValueForOption<string>("--db-password")!,
        DbName              = p.GetValueForOption<string>("--db-name")!,
        TrackerPassword     = p.GetValueForOption<string>("--tracker-password")!,
        TimetrackerTag      = p.GetValueForOption<string>("--tt-tag")!,
        DryRun              = p.GetValueForOption<bool>("--dry-run"),
        TimetrackerCpu      = p.GetValueForOption<double>("--tt-cpu"),
        TimetrackerMemoryGi = p.GetValueForOption<double>("--tt-memory"),
        DbCpu               = p.GetValueForOption<double>("--db-cpu"),
        DbMemoryGi          = p.GetValueForOption<double>("--db-memory"),
        RedisCpu            = p.GetValueForOption<double>("--redis-cpu"),
        RedisMemoryGi       = p.GetValueForOption<double>("--redis-memory"),
        Verbose             = p.GetValueForOption<bool>("--verbose")
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
}, deploy);

root.AddCommand(deploy);
return await root.InvokeAsync(args);