using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Mcp.Tools;

/// <summary>
/// stdio の generate_compose: docker-compose.yml と .env を、利用者の PC の output_dir に書き込む（Phase 3a）。
/// HTTP ではサーバーのファイルシステムに書いても利用者は使えないため、内容を返す <see cref="ComposeContentTools"/> を使う。
/// </summary>
[McpServerToolType]
public sealed class ComposeFileTools
{
    private readonly McpServerSettings _settings;
    private readonly ILogger<ComposeFileTools> _logger;

    public ComposeFileTools(McpServerSettings settings, ILogger<ComposeFileTools> logger)
    {
        _settings = settings;
        _logger = logger;
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
        var errors = DeploymentValidator.GetErrors(resolved.Options, ValidationTarget.Compose).Concat(ToolSupport.PasswordHints(resolved, _settings)).ToList();
        if (errors.Count > 0)
            throw new McpException("設定に誤りがあるため、ファイルを生成しませんでした:\n- " + string.Join("\n- ", errors));

        var composePath = Path.Combine(directory, DeploymentTools.ComposeFileName);
        var envPath = Path.Combine(directory, DeploymentTools.EnvFileName);
        if (!overwrite && (File.Exists(composePath) || File.Exists(envPath)))
            throw new McpException(
                $"{directory} に {DeploymentTools.ComposeFileName} または {DeploymentTools.EnvFileName} が既にあるため、上書きしませんでした。" +
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
        if (_settings.OutputRoot is { } limit)
            RejectLinksUnder(limit, directory);
        return directory;
    }

    // パスの文字列だけでは、配下にある既存のシンボリックリンク・ジャンクションの先（TT_MCP_OUTPUT_ROOT の外）へ書き込めてしまう。
    // そのため、ルートより下の各階層と、書き込むファイルがリンクでないことを確かめる（ルート自体はリンクでもよい）
    private static void RejectLinksUnder(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory);
        var current = root;
        var paths = new List<string>();
        if (relative != ".")
            foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                paths.Add(current = Path.Combine(current, part));
        paths.Add(Path.Combine(directory, DeploymentTools.ComposeFileName));
        paths.Add(Path.Combine(directory, DeploymentTools.EnvFileName));

        foreach (var path in paths)
            if (new FileInfo(path).LinkTarget is not null)
                throw new McpException($"{path} はシンボリックリンク（またはジャンクション）のため、書き込みませんでした（TT_MCP_OUTPUT_ROOT の外に書き込まないため）。");
    }
}
