using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Timetracker.Controller.Mcp.Prompts;

/// <summary>
/// MCP の Prompts（全体設計 5.3）。よく使う手順を、LLM への指示として返す。
/// 案内にすぎず、安全策（計画 → 確認 → 実行、同意の引数）はツールの側で強制している。
/// 引数は会話から渡る未検証の値なので、文に埋め込むだけにする（ツールに渡すときにツール側で検証される）。
/// </summary>
[McpServerPromptType]
public sealed class TimetrackerPrompts
{
    [McpServerPrompt(Name = "deploy-timetracker", Title = "Timetracker を Azure にデプロイする")]
    [Description("設定の確認 → サブスクリプションの選択 → 検証 → 計画 → 利用者の確認 → 実行 → 状態の確認の手順を案内する。")]
    public static string DeployTimetracker(
        [Description("DB 種別の希望（postgresql または sqlserver。省略時は利用者に確認する）")] string? db_type = null) => $"""
        Timetracker の検証環境を Azure Container Apps にデプロイします。次の順に進めてください。
        1. get_deployment_options を呼び、既定値・注意事項・サーバーの設定（server.password_env など）を確認する。
           検証環境向けであり、DB のデータは永続化されないことを利用者に伝える。
        2. list_subscriptions でサブスクリプションを示し、利用者にデプロイ先のサブスクリプションとリソースグループを選んでもらう。
        3. DB 種別は {(string.IsNullOrWhiteSpace(db_type) ? "利用者に確認する（既定は postgresql。サンプル DB を使うなら postgresql）" : $"利用者の希望「{db_type}」を使う")}。
           SQL Server を Azure で使う場合はサンプル DB を作れないことを伝える。
        4. validate_deployment で設定を検証し、errors があれば利用者と相談して直す。warnings は利用者に伝える。
        5. plan_deployment で計画を作り、actions・warnings・blockers・estimated_duration を利用者に示して、実行してよいか確認する。
           requires_data_loss_ack が true の場合は、DB のデータが失われることを伝え、利用者が明示的に同意した場合だけ
           start_deployment に acknowledge_data_loss=true を指定する。
        6. start_deployment に plan_id を渡し、get_job_status を 15〜30 秒の間隔で呼んで、結果の Timetracker の URL を伝える。
        7. get_deployment_info で状態を確認する。
        パスワードは、サーバーの環境変数で設定されていれば利用者に尋ねないこと。
        """;

    [McpServerPrompt(Name = "local-dry-run", Title = "ローカルの Docker で試す（Dry-run）")]
    [Description("設定の確認 → 検証 → generate_compose → docker compose up の手順を案内する。Azure は使わない。")]
    public static string LocalDryRun(
        [Description("docker-compose.yml と .env を書き出すディレクトリ（絶対パス。省略時は利用者に確認する）")] string? output_dir = null) => $"""
        Timetracker をローカルの Docker で試します（Azure は使いません）。次の順に進めてください。
        1. get_deployment_options を呼び、既定値とサーバーの設定（server.password_env、server.output_root）を確認する。
        2. 出力先は {(string.IsNullOrWhiteSpace(output_dir) ? "利用者に絶対パスを確認する" : $"「{output_dir}」を使う（利用者に確認してから）")}。
           server.output_root が設定されていれば、その配下にする。
        3. validate_deployment に target=compose を指定して検証し、errors があれば直す。
        4. generate_compose で docker-compose.yml と .env を生成する。既にファイルがある場合は、上書きしてよいか利用者に確認してから
           overwrite=true を指定する（.env の以前のパスワードは失われる）。.env にはパスワードが平文で書かれるため、共有しないよう伝える。
        5. 結果の next_steps のとおり、利用者に docker compose up -d を実行してもらい、http://localhost:8080 を開くよう案内する。
        """;

    [McpServerPrompt(Name = "delete-timetracker", Title = "Azure から Timetracker を削除する")]
    [Description("計画 → 対象と残るものを示して確認 → アプリ名の確認 → 実行 → 状態の確認の手順を案内する。")]
    public static string DeleteTimetracker(
        [Description("削除するアプリ名（省略時は利用者に確認する）")] string? app_name = null) => $"""
        Azure 上の Timetracker の検証環境を削除します。次の順に進めてください。
        1. 対象のサブスクリプション・リソースグループ・アプリ名（{(string.IsNullOrWhiteSpace(app_name) ? "利用者に確認する" : $"「{app_name}」")}）を利用者に確認する。
        2. plan_deletion で計画を作り、targets（削除するもの・しないものと理由）、kept（リソースグループは削除しない）、warnings、blockers を利用者に示す。
           DB アプリを削除する場合は、DB のデータがすべて失われることを伝える。
        3. 削除してよいか確認し、利用者にアプリ名を入力してもらう。入力されたアプリ名を start_deletion の confirm_app_name に渡す
           （利用者の入力なしに、計画のアプリ名をそのまま渡さないこと）。
        4. get_job_status を 15〜30 秒の間隔で呼び、deletion_result を伝える。
        5. 課金を完全に止めたい場合は、ほかのリソースを巻き込まないことを確かめてから、リソースグループごと削除する方法（Azure ポータルや az group delete）を案内する。
        """;
}
