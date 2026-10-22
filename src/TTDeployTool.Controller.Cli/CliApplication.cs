using TTDeployTool.Application.Deletion;
using TTDeployTool.Application.Deployment;
using TTDeployTool.Application.Permissions;
using TTDeployTool.Application.Workflows;
using TTDeployTool.Common;
using TTDeployTool.Domain.Deployment;
using TTDeployTool.Infrastructure.Azure;

namespace TTDeployTool.Controller.Cli;

/// <summary>
/// CLI の処理本体（引数の解析 → 検証 → Dry-run・デプロイ・削除）。
/// テストで入出力・Dry-run の出力ディレクトリ・Azure の操作を差し替えられるよう Program から分離している。
/// </summary>
public static class CliApplication
{
    /// <summary>デプロイ・削除で使う Azure の操作を作る。</summary>
    public delegate IAzureDeploymentGateway GatewayFactory(DeployOptions opts, IDeploymentLogger logger);

    /// <summary>--auth-mode に応じた資格情報で Azure SDK を使う。</summary>
    public static readonly GatewayFactory DefaultGatewayFactory =
        (opts, logger) => new AzureSdkExecutor(logger, CredentialFactory.Create(opts.AuthMode, logger));

    // 入出力先などの実行環境（RunAsync の引数をまとめたもの）
    private sealed record Context(
        TextWriter Output, TextWriter Error, TextWriter? OutputWriter, TextWriter? ErrorWriter,
        TextReader Input, string WorkingDirectory, GatewayFactory GatewayFactory, TimeProvider Time);

    /// <param name="args">コマンドライン引数</param>
    /// <param name="outputWriter">標準出力（省略時は Console.Out）</param>
    /// <param name="errorWriter">標準エラー出力（省略時は Console.Error）</param>
    /// <param name="workingDirectory">Dry-run のファイルの出力先（既定の "" はカレントディレクトリ）</param>
    /// <param name="gatewayFactory">Azure の操作（省略時は <see cref="DefaultGatewayFactory"/>）</param>
    /// <param name="inputReader">delete の確認の入力（省略時は Console.In）</param>
    /// <param name="time">delete でリソースグループが空になるのを待つときの時刻（省略時はシステムの時刻）</param>
    /// <returns>終了コード（成功 0 / 失敗・中止 1）</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? outputWriter = null,
        TextWriter? errorWriter = null,
        string workingDirectory = "",
        GatewayFactory? gatewayFactory = null,
        TextReader? inputReader = null,
        TimeProvider? time = null)
    {
        var ctx = new Context(
            outputWriter ?? Console.Out, errorWriter ?? Console.Error, outputWriter, errorWriter,
            inputReader ?? Console.In, workingDirectory, gatewayFactory ?? DefaultGatewayFactory, time ?? TimeProvider.System);

        CliArgs cli;
        try
        {
            cli = CliArgs.Parse(args);
        }
        catch (ArgumentException ex)
        {
            ctx.Error.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }

        if (cli.IsHelp)
        {
            ctx.Output.WriteLine(CliArgs.HelpText);
            return 0;
        }

        return cli.Command == CliCommand.Delete
            ? await RunDeleteAsync(cli, ctx)
            : await RunDeployAsync(cli, ctx);
    }

    private static async Task<int> RunDeployAsync(CliArgs cli, Context ctx)
    {
        // 必須オプション。Dry-run は Azure を使わないため subscription / resource-group は不要
        var requiredOptions = cli.GetBool("--dry-run")
            ? new[] { "--db-password", "--tracker-password" }
            : new[] { "--subscription", "--resource-group", "--db-password", "--tracker-password" };
        if (!CheckRequired(cli, requiredOptions, ctx))
            return 1;

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
                PersistDb           = cli.GetBoolOrNull("--persist-db"),
                ExpiresOn           = cli.Get("--expires-on"),
            };
        }
        catch (ArgumentException ex)
        {
            ctx.Error.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }

        var opts = DeployOptionsResolver.Resolve(input);
        if (!CheckAppName(cli, opts.AppName, ctx))
            return 1;

        var log = new SimpleLogger(opts.Verbose, ctx.OutputWriter, ctx.ErrorWriter);
        try
        {
            if (opts.DryRun)
            {
                ComposeGenerator.ValidateForCompose(opts);
                log.Info("検証 OK");
                if (opts.PersistDb == true)
                    log.Warn(DeploymentAdvisor.GetWarnings(opts, ValidationTarget.Compose).Single(w => w.StartsWith("persist-db")));
                var compose = ComposeGenerator.GenerateCompose(opts);
                await File.WriteAllTextAsync(Path.Combine(ctx.WorkingDirectory, opts.OutputCompose), compose);
                log.Info($"Compose 生成: {opts.OutputCompose}");
                var envContent = ComposeGenerator.GenerateEnv(opts);
                await File.WriteAllTextAsync(Path.Combine(ctx.WorkingDirectory, ".env"), envContent);
                log.Info(".env 生成: .env");
                log.Warn("Dry-run のため Container Apps デプロイは行いません。");
                return 0;
            }

            ComposeGenerator.Validate(opts);
            log.Info("検証 OK");
            var gateway = ctx.GatewayFactory(opts, log);

            var permissions = await new DeploymentPlanner(gateway).CheckPermissionsAsync(opts);
            if (!ReportPermissions(permissions, log))
                return 1;

            var result = await new DeploymentRunner(gateway, ctx.Time).RunAsync(opts);
            if (result.StorageAccountName != null)
                log.Info(DbPersistence.StorageNotice(result.StorageAccountName));
            log.Info(ExpiryNotice(result.Expiry!, ctx.Time));
            log.Success("Container Apps デプロイ完了");
            return 0;
        }
        catch (Exception ex)
        {
            log.Exception(ex);
            return 1;
        }
    }

    private static async Task<int> RunDeleteAsync(CliArgs cli, Context ctx)
    {
        if (!CheckRequired(cli, new[] { "--subscription", "--resource-group" }, ctx))
            return 1;

        // 資格情報の作成（--auth-mode）とアプリ名の既定値の適用に、deploy と同じ解決処理を使う
        var opts = DeployOptionsResolver.Resolve(new DeployInput
        {
            Subscription  = cli.Get("--subscription"),
            ResourceGroup = cli.Get("--resource-group"),
            AppName       = cli.Get("--app-name"),
            AuthMode      = cli.Get("--auth-mode"),
            Verbose       = cli.GetBool("--verbose"),
        });
        if (!CheckAppName(cli, opts.AppName, ctx))
            return 1;

        var log = new SimpleLogger(opts.Verbose, ctx.OutputWriter, ctx.ErrorWriter);
        try
        {
            var gateway = ctx.GatewayFactory(opts, log);
            var plan = await new DeletionPlanner(gateway).PlanAsync(
                opts.Subscription, opts.ResourceGroup, opts.AppName, cli.GetBool("--include-untagged"), cli.GetBool("--keep-db-data"),
                cli.GetBool("--delete-resource-group"));
            WritePlan(plan, log);

            if (cli.GetBool("--dry-run"))
            {
                log.Warn("Dry-run のため削除は行いません。");
                return 0;
            }
            if (plan.ResourceGroupBlocker is { } resourceGroupBlocker)
            {
                log.Error(resourceGroupBlocker.Replace("リソースグループの削除の指定", "--delete-resource-group の指定"));
                return 1;
            }
            if (!plan.HasDeletions)
            {
                log.Info("削除するリソースはありません。");
                return 0;
            }
            if (!ReportPermissions(plan.PermissionCheck, log))
                return 1;

            if (!cli.GetBool("--yes"))
            {
                ctx.Output.Write($"削除するには、アプリ名 '{opts.AppName}' を入力してください: ");
                if (ctx.Input.ReadLine()?.Trim() != opts.AppName)
                {
                    log.Error("入力がアプリ名と一致しないため、削除を中止しました。");
                    return 1;
                }
            }

            var runner = new DeletionRunner(gateway, ctx.Time);
            var results = await runner.RunAsync(
                plan, new LogProgress<DeletionTarget>(t => log.Info($"削除中: {t.Name}")));
            foreach (var result in results.Where(r => !r.Deleted))
            {
                log.Info($"{result.Name} は既に削除されていました。");
            }
            if (plan.DeletesResourceGroup)
            {
                log.Info($"削除中: リソースグループ {plan.ResourceGroup}");
                if (!await runner.DeleteResourceGroupAsync(plan))
                    log.Info($"リソースグループ {plan.ResourceGroup} は既に削除されていました。");
            }
            log.Success(DeletionWorkflow.CompletedMessage(plan));
            if (plan.KeepsDbData)
                log.Info(KeptDbDataNotice(plan));
            return 0;
        }
        catch (Exception ex)
        {
            log.Exception(ex);
            return 1;
        }
    }

    private static bool CheckRequired(CliArgs cli, IEnumerable<string> requiredOptions, Context ctx)
    {
        var missing = requiredOptions.Where(name => cli.Get(name) == null).ToList();
        if (missing.Count == 0)
            return true;

        ctx.Error.WriteLine($"[ERROR] 必須パラメータ不足: {string.Join(" ", missing)} を指定してください。（--help で使い方を表示）");
        return false;
    }

    private static bool CheckAppName(CliArgs cli, string appName, Context ctx)
    {
        var appNameError = AppNameValidator.GetError(appName);
        if (appNameError == null)
            return true;

        // 小文字化する前の、利用者が指定したとおりの名前で表示する
        var rawAppName = cli.Get("--app-name", Defaults.DefaultAppName);
        ctx.Error.WriteLine($"[ERROR] アプリ名 '{rawAppName}' は無効です: {appNameError}");
        ctx.Error.WriteLine("  - 英小文字 (a-z)、数字 (0-9)、ハイフン (-) のみ使用可能");
        ctx.Error.WriteLine("  - 英小文字で始まる/終わる必要があります（終端は英小文字または数字）");
        ctx.Error.WriteLine($"  - 連続するハイフン '--' は不可 / 長さ {AppNameValidator.MinLength}〜{AppNameValidator.MaxLength} 文字");
        return false;
    }

    /// <returns>処理を続けてよい場合 true（権限が不足している場合 false）</returns>
    private static bool ReportPermissions(PermissionCheckResult result, IDeploymentLogger log)
    {
        switch (result.Status)
        {
            case PermissionCheckStatus.Missing:
                log.Error($"権限が不足しています: {string.Join(", ", result.MissingActions)}。" +
                          "README の「必要な RBAC ロールと権限」を参照し、ロールの割り当てを確認してください。");
                return false;
            case PermissionCheckStatus.NotChecked:
                log.Verbose($"権限の事前確認をスキップしました: {result.Note}");
                return true;
            default:
                log.Verbose("権限の事前確認: OK");
                return true;
        }
    }

    private static void WritePlan(DeletionPlan plan, IDeploymentLogger log)
    {
        log.Info(plan.DeleteResourceGroupRequested ? "削除計画" : $"削除計画（リソースグループ {plan.ResourceGroup} は削除しません）");
        foreach (var t in plan.Targets)
        {
            var kind = t.Kind switch
            {
                DeletionResourceKind.ContainerApp => "Container App",
                DeletionResourceKind.Environment => "Environment",
                _ => "ストレージアカウント",
            };
            log.Info(t.WillDelete
                ? $"  削除する     {t.Name} ({kind}){(t.Managed ? "" : " ※管理タグなし")}"
                : $"  削除しない   {t.Name} ({kind}): {t.Reason}");
        }
        if (plan.ResourceGroupTarget is { } rg)
            log.Info(rg.WillDelete
                ? $"  削除する     {plan.ResourceGroup} (リソースグループ)"
                : $"  削除しない   {plan.ResourceGroup} (リソースグループ): {rg.Reason}");
        if (plan.HasUnmanaged)
            log.Info("  管理タグの無いリソースも削除するには --include-untagged を指定してください。");
        if (plan.DeletesResourceGroup)
            log.Warn($"リソースグループ {plan.ResourceGroup} も削除します。元に戻せません。");
        if (plan.LosesDbData)
            log.Warn("DB アプリを削除すると DB のデータはすべて失われます。");
        if (plan.DeletesDbData)
            log.Warn($"ストレージアカウント {plan.StorageAccountName}（Azure Files に永続化した DB のデータ）も削除します。元に戻せません（残すには --keep-db-data）。");
        if (plan.KeepsDbData)
            log.Info(KeptDbDataNotice(plan));
        if (plan.PermissionCheck.Status == PermissionCheckStatus.Missing)
            log.Warn($"権限が不足しています: {string.Join(", ", plan.PermissionCheck.MissingActions)}");
        if (plan.PermissionCheck.Status == PermissionCheckStatus.NotChecked)
            log.Warn($"権限を事前に確認できませんでした: {plan.PermissionCheck.Note}");
    }

    /// <summary>記録した利用可能期限の案内。</summary>
    internal static string ExpiryNotice(ExpiryDecision expiry, TimeProvider time) => expiry.ExpiresAt == null
        ? $"利用可能期限: {expiry.Describe(time.LocalTimeZone)}"
        : $"利用可能期限: {expiry.Describe(time.LocalTimeZone)}。期限を過ぎると、TTDeployTool のデスクトップアプリが自動で削除します" +
          "（DB のデータも消えます。CLI は期限での削除をしません）。変えるには --expires-on <yyyy-MM-dd|none> を付けてデプロイしてください。";

    private static string KeptDbDataNotice(DeletionPlan plan) =>
        $"ストレージアカウント {plan.StorageAccountName}（DB のデータ）は残します。料金がかかり続けます。同じアプリ名で --persist-db を付けて" +
        "デプロイするとデータを引き継ぎます。不要になったら --keep-db-data を付けずに delete を実行してください。";

    // Progress<T> は SynchronizationContext 経由で非同期に通知するため、処理の順にログを出せるよう同期的に呼ぶ
    private sealed class LogProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
