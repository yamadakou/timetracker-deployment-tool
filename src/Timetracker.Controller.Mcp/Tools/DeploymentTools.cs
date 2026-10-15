using System.ComponentModel;
using Azure;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Timetracker.Domain.Deployment;
using Timetracker.Infrastructure.Azure;

namespace Timetracker.Controller.Mcp.Tools;

/// <summary>
/// 設定の確認・検証・Dry-run・サブスクリプションの一覧のツール（Phase 3a）。
/// 引数名は CLI のオプションと 1 対 1 の snake_case（SDK は C# の引数名をそのままツールの引数名にするため、引数名を snake_case にしている）。
/// 入力の誤りは、LLM が自分で直せるよう、プロトコルのエラーではなくツールの結果（valid: false や isError）で返す。
/// </summary>
[McpServerToolType]
public sealed class DeploymentTools
{
    public const string ComposeFileName = "docker-compose.yml";
    public const string EnvFileName = ".env";
    public const string MaskedPassword = "********";

    internal static readonly IReadOnlyList<string> Constraints =
    [
        "検証環境向けのツールです。Azure 上の DB のデータは永続化されません（DB コンテナの再起動・再デプロイ・削除で消えます）。",
        "SQL Server は Developer エディションです（本番環境では使えません）。",
        "Azure 上の SQL Server 構成ではサンプル DB を作成できません（Timetracker のセットアップで空の DB を選択してください）。",
        "Timetracker のメモリが 2Gi 以下だと、サンプル DB の作成が OutOfMemory で失敗することがあります。",
    ];

    // 認証に失敗したときの対処（認証モードごと）
    private static readonly IReadOnlyDictionary<string, string> AuthHints = new Dictionary<string, string>
    {
        ["azure-cli"] = "az login でサインインしてから、もう一度試してください。",
        ["default"] = "az login などでサインインしてから、もう一度試してください（DefaultAzureCredential が使える資格情報が見つかりませんでした）。",
        ["sp-env"] = "サーバーの環境変数 AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET を確認してください。",
        ["device-code"] = "サーバーのログ（stderr）に表示されるコードで、ブラウザーからサインインしてください。",
        ["managed-identity"] = "マネージド ID が使える Azure 上の環境で実行しているか確認してください。",
    };

    private readonly McpServerSettings _settings;
    private readonly Func<ISubscriptionDirectory> _subscriptionDirectory;
    private readonly ILogger<DeploymentTools> _logger;

    /// <param name="subscriptionDirectory">資格情報の作成に失敗した場合もツールの結果として返せるよう、使うときに作る</param>
    public DeploymentTools(McpServerSettings settings, Func<ISubscriptionDirectory> subscriptionDirectory, ILogger<DeploymentTools> logger)
    {
        _settings = settings;
        _subscriptionDirectory = subscriptionDirectory;
        _logger = logger;
    }

    [McpServerTool(Name = "get_deployment_options", Title = "デプロイ設定の既定値と制約",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("デプロイ設定の既定値、選べる値（DB 種別と既定のイメージタグ）、アプリ名の規則、作成するリソース名の接尾辞、注意事項、" +
                 "サーバーの設定（認証モード、パスワードの環境変数が設定済みか）を返す。Azure には接続しない。最初に呼んで前提を確認する。")]
    public DeploymentOptionsResult GetDeploymentOptions() => new(
        DbTypes: ["postgresql", "sqlserver"],
        DefaultTtTags: new Dictionary<string, string>
        {
            ["postgresql"] = Defaults.GetDefaultTimetrackerTag("postgresql"),
            ["sqlserver"] = Defaults.GetDefaultTimetrackerTag("sqlserver"),
        },
        Defaults: new DefaultValues(
            Defaults.DefaultLocation, Defaults.DefaultAppName, DeployOptionsResolver.DefaultDbType, Defaults.DbName,
            Defaults.TimetrackerCpu, Defaults.TimetrackerMemoryGi, Defaults.DbCpu, Defaults.DbMemoryGi, Defaults.RedisCpu, Defaults.RedisMemoryGi),
        AppNameRule: new AppNameRule(AppNameValidator.MinLength, AppNameValidator.MaxLength, AppNameValidator.Pattern, NoDoubleHyphen: true),
        ResourceSuffixes: ResourceNaming.Suffixes,
        Server: new ServerStatus(
            Transport: "stdio",
            AuthMode: _settings.AuthMode,
            PasswordEnv: new PasswordEnvStatus(_settings.DbPassword != null, _settings.TrackerPassword != null),
            DefaultSubscriptionSet: _settings.DefaultSubscription != null,
            DefaultResourceGroupSet: _settings.DefaultResourceGroup != null,
            OutputRoot: _settings.OutputRoot),
        Constraints: Constraints,
        TagListUrl: "https://hub.docker.com/r/densocreate/timetracker/tags");

    [McpServerTool(Name = "validate_deployment", Title = "デプロイ設定の検証",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("デプロイ設定を検証し、すべてのエラーと警告、既定値を適用した設定（パスワードはマスク）、作成するリソース名を返す。" +
                 "Azure には接続しない。target=azure（既定）は Azure へのデプロイ用（subscription と resource_group が必須）、" +
                 "target=compose はローカルの Docker 用（generate_compose）。省略した項目には既定値を使う。")]
    public ValidationResult ValidateDeployment(
        [Description("検証の対象。azure（既定）または compose")] string? target = null,
        [Description("サブスクリプション ID（省略時はサーバーの既定値 TT_MCP_DEFAULT_SUBSCRIPTION）")] string? subscription = null,
        [Description("リソースグループ名（省略時はサーバーの既定値 TT_MCP_DEFAULT_RESOURCE_GROUP）")] string? resource_group = null,
        [Description("リージョン（既定 japaneast）")] string? location = null,
        [Description("アプリ名（既定 timetracker）。リソース名の接頭辞になる")] string? app_name = null,
        [Description("DB 種別: postgresql（既定）または sqlserver")] string? db_type = null,
        [Description("DB 名（既定 timetracker）")] string? db_name = null,
        [Description("timetracker イメージのタグ（既定は DB 種別に応じたタグ）")] string? tt_tag = null,
        [Description("DB のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_DB_PASSWORD での設定を推奨")] string? db_password = null,
        [Description("Timetracker のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_TRACKER_PASSWORD での設定を推奨")] string? tracker_password = null,
        [Description("Timetracker の CPU（vCPU、既定 1.5）")] double? tt_cpu = null,
        [Description("Timetracker のメモリ（Gi、既定 3.0）")] double? tt_memory_gi = null,
        [Description("DB の CPU（vCPU、既定 1.0）")] double? db_cpu = null,
        [Description("DB のメモリ（Gi、既定 2.0）")] double? db_memory_gi = null,
        [Description("Redis の CPU（vCPU、既定 0.5）")] double? redis_cpu = null,
        [Description("Redis のメモリ（Gi、既定 1.0）")] double? redis_memory_gi = null)
    {
        var parameters = new DeploymentParameters
        {
            Subscription = subscription, ResourceGroup = resource_group, Location = location, AppName = app_name, DbType = db_type,
            DbName = db_name, TtTag = tt_tag, DbPassword = db_password, TrackerPassword = tracker_password,
            TtCpu = tt_cpu, TtMemoryGi = tt_memory_gi, DbCpu = db_cpu, DbMemoryGi = db_memory_gi, RedisCpu = redis_cpu, RedisMemoryGi = redis_memory_gi,
        };
        var resolved = parameters.Resolve(_settings);
        var (validationTarget, targetError) = ParseTarget(target);
        var errors = new List<string>();
        if (targetError != null)
            errors.Add(targetError);
        errors.AddRange(DeploymentValidator.GetErrors(resolved.Options, validationTarget));
        errors.AddRange(PasswordHints(resolved));
        var o = resolved.Options;
        return new ValidationResult(
            Valid: errors.Count == 0,
            Errors: errors,
            Warnings: DeploymentAdvisor.GetWarnings(o, validationTarget),
            Resolved: ToResolvedSettings(resolved),
            ResourceNames: new ResourceNames(
                ResourceNaming.Environment(o.AppName), ResourceNaming.TimetrackerApp(o.AppName), ResourceNaming.DbApp(o.AppName), ResourceNaming.RedisApp(o.AppName)));
    }

    [McpServerTool(Name = "generate_compose", Title = "ローカル用の docker-compose.yml と .env の生成（Dry-run）",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("ローカルの Docker で動かすための docker-compose.yml と .env を output_dir に書き込む（CLI の --dry-run と同じ内容）。" +
                 "Azure には接続せず、subscription と resource_group は不要。.env はパスワードを含むため内容を返さない。" +
                 "既存のファイルは overwrite=true のときだけ上書きする。")]
    public ComposeResult GenerateCompose(
        [Description("出力先ディレクトリの絶対パス（無ければ作成する）")] string output_dir,
        [Description("既存の docker-compose.yml / .env を上書きするか（既定 false）")] bool overwrite = false,
        [Description("リージョン（Dry-run では使わない）")] string? location = null,
        [Description("アプリ名（既定 timetracker）")] string? app_name = null,
        [Description("DB 種別: postgresql（既定）または sqlserver")] string? db_type = null,
        [Description("DB 名（既定 timetracker）")] string? db_name = null,
        [Description("timetracker イメージのタグ（既定は DB 種別に応じたタグ）")] string? tt_tag = null,
        [Description("DB のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_DB_PASSWORD での設定を推奨")] string? db_password = null,
        [Description("Timetracker のパスワード。会話に残るため、サーバーの環境変数 TT_MCP_TRACKER_PASSWORD での設定を推奨")] string? tracker_password = null,
        [Description("Timetracker の CPU（Dry-run では使わない）")] double? tt_cpu = null,
        [Description("Timetracker のメモリ（Gi。Dry-run では使わない）")] double? tt_memory_gi = null,
        [Description("DB の CPU（Dry-run では使わない）")] double? db_cpu = null,
        [Description("DB のメモリ（Gi。Dry-run では使わない）")] double? db_memory_gi = null,
        [Description("Redis の CPU（Dry-run では使わない）")] double? redis_cpu = null,
        [Description("Redis のメモリ（Gi。Dry-run では使わない）")] double? redis_memory_gi = null)
    {
        var directory = ResolveOutputDirectory(output_dir);
        var resolved = new DeploymentParameters
        {
            Location = location, AppName = app_name, DbType = db_type, DbName = db_name, TtTag = tt_tag,
            DbPassword = db_password, TrackerPassword = tracker_password,
            TtCpu = tt_cpu, TtMemoryGi = tt_memory_gi, DbCpu = db_cpu, DbMemoryGi = db_memory_gi, RedisCpu = redis_cpu, RedisMemoryGi = redis_memory_gi,
        }.Resolve(_settings);
        var errors = DeploymentValidator.GetErrors(resolved.Options, ValidationTarget.Compose).Concat(PasswordHints(resolved)).ToList();
        if (errors.Count > 0)
            throw new McpException("設定に誤りがあるため、ファイルを生成しませんでした:\n- " + string.Join("\n- ", errors));

        var composePath = Path.Combine(directory, ComposeFileName);
        var envPath = Path.Combine(directory, EnvFileName);
        if (!overwrite && (File.Exists(composePath) || File.Exists(envPath)))
            throw new McpException(
                $"{directory} に {ComposeFileName} または {EnvFileName} が既にあるため、上書きしませんでした。" +
                "上書きしてよい場合は overwrite=true を指定してください（.env の以前のパスワードは失われます）。");

        var compose = ComposeGenerator.GenerateCompose(resolved.Options);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(composePath, compose);
            File.WriteAllText(envPath, ComposeGenerator.GenerateEnv(resolved.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"{directory} に書き込めませんでした: {ex.Message}");
        }
        _logger.LogInformation("docker-compose.yml と .env を生成しました: {Directory}", directory);

        return new ComposeResult(
            ComposePath: composePath,
            EnvPath: envPath,
            ComposeContent: compose,
            Warnings: DeploymentAdvisor.GetWarnings(resolved.Options, ValidationTarget.Compose),
            NextSteps:
            [
                $"{directory} で docker compose up -d を実行する",
                "ブラウザーで http://localhost:8080 を開く（初回は起動に数分かかる）",
                "停止するときは docker compose down（データも消す場合は docker compose down -v）",
            ]);
    }

    [McpServerTool(Name = "list_subscriptions", Title = "Azure サブスクリプションの一覧",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("サーバーの認証モード（TT_MCP_AUTH_MODE）の資格情報で使える Azure サブスクリプションの一覧を返す。" +
                 "利用者にデプロイ先のサブスクリプションを選んでもらうときに使う。")]
    public async Task<SubscriptionsResult> ListSubscriptions(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubscriptionInfo> subscriptions;
        try
        {
            subscriptions = await _subscriptionDirectory().ListAsync(cancellationToken);
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogWarning(ex, "Azure の認証に失敗しました");
            throw new McpException($"Azure の認証に失敗しました（認証モード: {_settings.AuthMode}）。{AuthHints[_settings.AuthMode]}");
        }
        catch (RequestFailedException ex)
        {
            throw new McpException($"Azure がエラーを返しました（{ex.Status} {ex.ErrorCode}）: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // 資格情報を作れない場合（sp-env で環境変数が足りないなど）。メッセージに秘密情報は含まれない
            throw new McpException(ex.Message);
        }

        return new SubscriptionsResult(
            _settings.AuthMode,
            subscriptions.Select(s => new SubscriptionItem(s.SubscriptionId, s.DisplayName, s.State, s.TenantId)).ToList());
    }

    private static (ValidationTarget Target, string? Error) ParseTarget(string? target) =>
        (target ?? "azure").ToLowerInvariant() switch
        {
            "azure" => (ValidationTarget.Azure, null),
            "compose" => (ValidationTarget.Compose, null),
            _ => (ValidationTarget.Azure, $"target '{target}' は無効です。azure または compose を指定してください。"),
        };

    // パスワードが無い場合、値を会話に出さずに済む方法（サーバーの環境変数）を案内する
    private static IEnumerable<string> PasswordHints(ResolvedParameters resolved)
    {
        if (resolved.DbPasswordSource == PasswordSource.None)
            yield return "db_password が未設定です。サーバーの環境変数 TT_MCP_DB_PASSWORD で設定するか（推奨。値が会話に残らない）、引数で指定してください。";
        if (resolved.TrackerPasswordSource == PasswordSource.None)
            yield return "tracker_password が未設定です。サーバーの環境変数 TT_MCP_TRACKER_PASSWORD で設定するか（推奨。値が会話に残らない）、引数で指定してください。";
    }

    private static string Mask(PasswordSource source) => source switch
    {
        PasswordSource.Argument => $"{MaskedPassword}（引数）",
        PasswordSource.Environment => $"{MaskedPassword}（環境変数）",
        _ => "（未設定）",
    };

    private static ResolvedSettings ToResolvedSettings(ResolvedParameters resolved)
    {
        var o = resolved.Options;
        return new ResolvedSettings(
            o.Subscription, o.ResourceGroup, o.Location, o.AppName, o.DbType, o.DbName, o.TimetrackerTag,
            Mask(resolved.DbPasswordSource), Mask(resolved.TrackerPasswordSource),
            o.TimetrackerCpu, o.TimetrackerMemoryGi, o.DbCpu, o.DbMemoryGi, o.RedisCpu, o.RedisMemoryGi);
    }

    // 出力先は絶対パスに限り、TT_MCP_OUTPUT_ROOT があればその配下に限る（".." を含むパスは正規化してから判定する）
    private string ResolveOutputDirectory(string outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir) || !Path.IsPathFullyQualified(outputDir))
            throw new McpException($"output_dir には出力先ディレクトリの絶対パスを指定してください: '{outputDir}'");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir));
        // 大文字小文字の違いは区別する（Windows で大文字小文字だけが違うパスは拒否される側に倒れる）。
        // ルートディレクトリ（"/" や "C:\"）は末尾の区切り文字が残るため、そのまま接頭辞にする
        if (_settings.OutputRoot is { } root
            && directory != root
            && !directory.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new McpException($"output_dir は {root} の配下を指定してください（サーバーの設定 TT_MCP_OUTPUT_ROOT で制限されています）: {directory}");
        return directory;
    }
}
