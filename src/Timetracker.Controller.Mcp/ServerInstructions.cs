namespace Timetracker.Controller.Mcp;

/// <summary>
/// 初期化時にクライアントへ渡す instructions（全体設計 5.1）。LLM が毎回 README を読まなくても、前提を守ってツールを使えるようにする。
/// 安全策はここに頼らず、サーバー側で強制する（クライアントやモデルによって instructions の扱いが違うため）。
/// </summary>
internal static class ServerInstructions
{
    public const string Text = """
        Timetracker（DENSO CREATE の工数管理ツール）の検証環境を構築するためのサーバーです。
        - 検証環境向けです。Azure 上の DB のデータは永続化されず、DB コンテナの再起動・再デプロイ・削除で消えます。本番環境には使わないでください。
        - 最初に get_deployment_options を呼び、既定値・選べる値・注意事項・サーバーの設定を確認してください。
        - パスワード（db_password / tracker_password）は、サーバーの環境変数での設定を推奨します（引数で渡すと会話に残ります）。
          get_deployment_options の server.password_env が true の項目は、利用者にパスワードを尋ねないでください。
        - 設定を決めたら validate_deployment で検証し、errors があれば利用者と相談して直してください。warnings は利用者に伝えてください。
        - ローカルの Docker で試す場合は generate_compose（Dry-run）で docker-compose.yml と .env を作ります。output_dir は利用者に確認した絶対パスにしてください。
        - SQL Server を Azure で使う場合はサンプル DB を作れません。Timetracker のメモリは 3Gi 以上を推奨します。
        - Azure へのデプロイは、必ず次の順に行ってください。
          1. plan_deployment で計画を作る（Azure は変更しない）。
          2. 結果の actions・warnings・blockers を利用者に示し、実行してよいか確認する。requires_data_loss_ack が true の場合は、
             既存の DB アプリを更新するため、設定が変わると DB コンテナが再起動して DB のデータが消えることを伝え、利用者が明示的に同意した場合だけ start_deployment に acknowledge_data_loss=true を指定する。
          3. start_deployment に plan_id を渡して始める。その後は get_job_status を 15〜30 秒の間隔で呼び、結果（Timetracker の URL）を伝える。
        - デプロイ済みの環境の状態や URL は get_deployment_info で確認できます。
        - この版では Azure から削除するツールはまだありません。必要な場合は CLI（timetracker-cli delete）を案内してください。
        """;
}
