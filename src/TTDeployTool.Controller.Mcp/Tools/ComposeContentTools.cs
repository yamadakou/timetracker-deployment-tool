using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TTDeployTool.Domain.Deployment;

namespace TTDeployTool.Controller.Mcp.Tools;

/// <summary>
/// HTTP の generate_compose（詳細設計 Phase 4a の 5）。サーバーのファイルシステムには書かず、docker-compose.yml の内容と、
/// パスワードをプレースホルダーにした .env の内容を返す。パスワードは会話に出さないよう、引数で受け取らない。
/// </summary>
[McpServerToolType]
public sealed class ComposeContentTools
{
    public const string DbPasswordPlaceholder = "<DB パスワードを設定>";
    public const string TrackerPasswordPlaceholder = "<Timetracker のパスワードを設定>";

    // 検証のための仮のパスワード（SQL Server のパスワード要件を満たす）。結果には出さない
    private const string ValidationPassword = "Placeholder-Passw0rd";

    private readonly McpServerSettings _settings;

    public ComposeContentTools(McpServerSettings settings)
    {
        _settings = settings;
    }

    [McpServerTool(Name = "generate_compose", Title = "ローカル用の docker-compose.yml と .env の内容の生成（Dry-run）",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("ローカルの Docker で動かすための docker-compose.yml と .env の内容を返す（CLI の --dry-run と同じ内容。ファイルは書き込まない）。" +
                 "Azure には接続せず、subscription と resource_group は不要。.env のパスワードはプレースホルダーにしてあるため、" +
                 "利用者が自分で設定する（パスワードを会話に出さないよう、引数では受け取らない）。")]
    public ComposeContentResult GenerateCompose(
        [Description("アプリ名（既定 timetracker）")] string? app_name = null,
        [Description("DB 種別: postgresql（既定）または sqlserver")] string? db_type = null,
        [Description("DB 名（既定 timetracker）")] string? db_name = null,
        [Description("timetracker イメージのタグ（既定は DB 種別に応じたタグ）")] string? tt_tag = null)
    {
        var o = new DeploymentParameters
        {
            AppName = app_name, DbType = db_type, DbName = db_name, TtTag = tt_tag,
            DbPassword = ValidationPassword, TrackerPassword = ValidationPassword,
        }.Resolve(_settings).Options;
        var errors = DeploymentValidator.GetErrors(o, ValidationTarget.Compose);
        if (errors.Count > 0)
            throw new McpException("設定に誤りがあるため、生成しませんでした:\n- " + string.Join("\n- ", errors));

        var warnings = DeploymentAdvisor.GetWarnings(o, ValidationTarget.Compose);
        o.DbPassword = DbPasswordPlaceholder;
        o.TrackerPassword = TrackerPasswordPlaceholder;
        var passwordRule = o.DbType == "sqlserver"
            ? "DB_PASSWORD は SQL Server のパスワード要件（8 文字以上で、英大文字・英小文字・数字・記号のうち 3 種類以上）を満たす値にする"
            : "DB_PASSWORD に任意のパスワードを設定する";
        return new ComposeContentResult(
            ComposeFileName: DeploymentTools.ComposeFileName,
            ComposeContent: ComposeGenerator.GenerateCompose(o),
            EnvFileName: DeploymentTools.EnvFileName,
            EnvContent: ComposeGenerator.GenerateEnv(o),
            Warnings: warnings,
            NextSteps:
            [
                $"同じディレクトリに {DeploymentTools.ComposeFileName} と {DeploymentTools.EnvFileName} を保存する",
                $"{DeploymentTools.EnvFileName} のプレースホルダーを置き換える: {passwordRule}。TIMETRACKER_PASSWORD にも設定する（.env は共有・コミットしない）",
                "そのディレクトリで docker compose up -d を実行する",
                "ブラウザーで http://localhost:8080 を開く（初回は起動に数分かかる）",
                "停止するときは docker compose down（データも消す場合は docker compose down -v）",
            ]);
    }
}
