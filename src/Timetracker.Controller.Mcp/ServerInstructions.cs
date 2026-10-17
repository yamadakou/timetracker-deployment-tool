namespace Timetracker.Controller.Mcp;

/// <summary>
/// 初期化時にクライアントへ渡す instructions（全体設計 5.1）。LLM が毎回 README を読まなくても、前提を守ってツールを使えるようにする。
/// 安全策はここに頼らず、サーバー側で強制する（クライアントやモデルによって instructions の扱いが違うため）。
/// </summary>
internal static class ServerInstructions
{
    public const string Text = """
        Timetracker（DENSO CREATE の工数管理ツール）の検証環境を構築するためのサーバーです。
        - 検証環境向けです。Azure 上の DB のデータは、既定では永続化されず、DB コンテナの再起動・再デプロイ・削除で消えます。本番環境には使わないでください。
          利用者がデータを残したい場合だけ、plan_deployment / validate_deployment に persist_db=true を指定すると、DB のデータを Azure Files に置きます（ストレージの料金がかかります）。
          persist_db を省略すると、既存の DB アプリの今の状態（永続化しているか）を引き継ぎます。
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
             永続化を続ける更新（persist_db が true のまま）では requires_data_loss_ack は false になり、DB アプリを停止してから更新します（その間は接続できません）。
          3. start_deployment に plan_id を渡して始める。その後は get_job_status を 15〜30 秒の間隔で呼び、結果（Timetracker の URL）を伝える。
        - デプロイ済みの環境の状態や URL は get_deployment_info で確認できます。
        - Azure から削除する場合も、必ず次の順に行ってください。
          1. plan_deletion で計画を作る（Azure は変更しない）。
          2. 結果の targets・kept・warnings・blockers を利用者に示す。リソースグループは削除しないこと、DB アプリを削除すると DB のデータがすべて失われることを伝える。
             persist_db で永続化したストレージアカウントも既定で削除されるため、データを残したい場合は keep_db_data=true で計画を作り直す（料金はかかり続ける）。
          3. 削除してよいか確認し、利用者にアプリ名を入力してもらい、その値を start_deletion の confirm_app_name に渡す（利用者の入力なしに計画のアプリ名をそのまま渡さない）。
          4. get_job_status を 15〜30 秒の間隔で呼び、結果を伝える。
        - 注意事項は timetracker://docs/constraints、必要な権限は timetracker://docs/rbac（Resources）でも読めます。
        """;

    /// <summary>
    /// HTTP の instructions。stdio との違い（Azure を操作する方式、パスワード、操作できる範囲、generate_compose）を伝える。
    /// 方式の説明は、サーバーで有効な方式（TT_MCP_HTTP__IDENTITY_MODES）に合わせる。
    /// </summary>
    public static string Http(McpHttpSettings settings) =>
        HttpText.Replace(IdentityPlaceholder, IdentityText(settings).TrimEnd('\r', '\n'));

    // 改行は含めない（Windows で CRLF でチェックアウトすると、raw 文字列の改行も CRLF になるため）
    private const string IdentityPlaceholder = "{identity}";

    private const string HttpText = """
        Timetracker（DENSO CREATE の工数管理ツール）の検証環境を構築するためのサーバーです（HTTP で共有しているサーバー）。
        - 検証環境向けです。Azure 上の DB のデータは、既定では永続化されず、DB コンテナの再起動・再デプロイ・削除で消えます。本番環境には使わないでください。
          利用者がデータを残したい場合だけ、plan_deployment / validate_deployment に persist_db=true を指定すると、DB のデータを Azure Files に置きます（ストレージの料金がかかります）。
          persist_db を省略すると、既存の DB アプリの今の状態（永続化しているか）を引き継ぎます。
        - 最初に get_deployment_options を呼び、既定値・選べる値・注意事項・サーバーの設定を確認してください。
        {identity}
        - パスワード（db_password / tracker_password）は、Azure へのデプロイでは引数で渡す必要があります（このサーバーではパスワードの環境変数は使えません）。
          会話に残ることを利用者に伝えてから尋ねてください。
        - 設定を決めたら validate_deployment で検証し、errors があれば利用者と相談して直してください。warnings は利用者に伝えてください。
        - ローカルの Docker で試す場合は generate_compose で docker-compose.yml と .env の内容を返します（このサーバーはファイルを書き込みません）。
          設定は generate_compose が検証するため、validate_deployment（パスワードを求める）は不要です。
          利用者に 2 つのファイルを保存してもらい、.env のパスワードのプレースホルダーは利用者自身に置き換えてもらってください。
        - SQL Server を Azure で使う場合はサンプル DB を作れません。Timetracker のメモリは 3Gi 以上を推奨します。
        - Azure へのデプロイは、必ず次の順に行ってください。
          1. plan_deployment で計画を作る（Azure は変更しない）。
          2. 結果の actions・warnings・blockers を利用者に示し、実行してよいか確認する。requires_data_loss_ack が true の場合は、
             既存の DB アプリを更新するため、設定が変わると DB コンテナが再起動して DB のデータが消えることを伝え、利用者が明示的に同意した場合だけ start_deployment に acknowledge_data_loss=true を指定する。
             永続化を続ける更新（persist_db が true のまま）では requires_data_loss_ack は false になり、DB アプリを停止してから更新します（その間は接続できません）。
          3. start_deployment に plan_id を渡して始める。その後は get_job_status を 15〜30 秒の間隔で呼び、結果（Timetracker の URL）を伝える。
        - デプロイ済みの環境の状態や URL は get_deployment_info で確認できます。
        - Azure から削除する場合も、必ず次の順に行ってください。
          1. plan_deletion で計画を作る（Azure は変更しない）。
          2. 結果の targets・kept・warnings・blockers を利用者に示す。リソースグループは削除しないこと、DB アプリを削除すると DB のデータがすべて失われることを伝える。
             persist_db で永続化したストレージアカウントも既定で削除されるため、データを残したい場合は keep_db_data=true で計画を作り直す（料金はかかり続ける）。
          3. 削除してよいか確認し、利用者にアプリ名を入力してもらい、その値を start_deletion の confirm_app_name に渡す（利用者の入力なしに計画のアプリ名をそのまま渡さない）。
          4. get_job_status を 15〜30 秒の間隔で呼び、結果を伝える。
        - 注意事項は timetracker://docs/constraints、必要な権限は timetracker://docs/rbac（Resources）でも読めます。
        """;

    // Azure を操作する方式の説明（server だけ・user だけ・両方）
    private static string IdentityText(McpHttpSettings settings) =>
        !settings.UserModeEnabled ? ServerOnlyText : !settings.ServerModeEnabled ? UserOnlyText : BothModesText;

    private const string ServerOnlyText = """
        - Azure はサーバーの ID（server 方式）で操作します。操作できるのは server.allowed_resource_groups のリソースグループだけです。
          server.can_create_resource_group が false の場合、リソースグループが無ければ管理者に作成を依頼するよう利用者に伝えてください。
          Azure を読む・操作するツールには、アプリロール Timetracker.Deploy.Server が必要です。

        """;

    private const string UserOnlyText = """
        - Azure は、サインインした利用者本人の Azure の権限（user 方式）で操作します。操作できるのは、利用者本人にロールが割り当てられた範囲です。
          権限が足りない場合は、計画の permission_check と blockers に表示されるので、利用者に伝えてください。
          Azure を読む・操作するツールには、アプリロール Timetracker.Deploy.User または Timetracker.Deploy.Server が必要です。

        """;

    private const string BothModesText = """
        - Azure を操作する方式は 2 つあります。server（サーバーの ID。server.allowed_resource_groups のリソースグループだけ）と、
          user（サインインした利用者本人の Azure の権限）です。list_subscriptions / plan_deployment / plan_deletion / get_deployment_info の
          azure_identity で選べます（省略時は server.auth_mode。利用者が使える方式は server.caller_identity_modes）。
          server 方式では、server.can_create_resource_group が false の場合、リソースグループが無ければ管理者に作成を依頼するよう利用者に伝えてください。
          計画を示すときは、結果の identity（どの権限で実行するか）を必ず利用者に伝えてください。start_deployment / start_deletion は計画の方式で実行します。
          server 方式にはアプリロール Timetracker.Deploy.Server、user 方式には Timetracker.Deploy.User または Timetracker.Deploy.Server が必要です。

        """;
}
