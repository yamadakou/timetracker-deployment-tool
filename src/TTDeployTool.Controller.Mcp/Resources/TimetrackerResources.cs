using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TTDeployTool.Controller.Mcp.Tools;

namespace TTDeployTool.Controller.Mcp.Resources;

/// <summary>
/// MCP の Resources（全体設計 5.2）。注意事項と必要な権限の文書（README の要約。配布物に README を含めないためサーバーに埋め込む）と、
/// デプロイ済みの環境の状態（get_deployment_info と同じ内容）を返す。
/// </summary>
[McpServerResourceType]
public sealed class TimetrackerResources
{
    public const string ConstraintsUri = "ttdeploytool://docs/constraints";
    public const string RbacUri = "ttdeploytool://docs/rbac";
    public const string DeploymentUriTemplate = "ttdeploytool://deployments/{subscription}/{resource_group}/{app_name}";

    public const string Constraints = """
        # 本ツールの目的と注意事項

        本ツールは、Timetracker の **検証環境（評価・動作確認・デモ用途）を手早く構築するための支援ツール** です。本番環境の構築は対象外です。

        | 項目 | 内容 |
        |---|---|
        | Azure 上の DB データは既定では永続化されない | 既定では、DB はボリュームを持たないコンテナとして動作します。DB コンテナが再起動すると（設定を変えた再デプロイ、リビジョンの更新、Azure 基盤のメンテナンスなど）、DB のデータは失われます。残したいデータは事前にエクスポートしてください。persist_db=true を指定すると、DB のデータを Azure Files（ストレージアカウント）に置き、再起動の後も残します（ストレージの容量とトランザクションの料金がかかります。plan_deletion では keep_db_data=true を指定しない限り、ストレージアカウントも削除します。まれにデータが壊れるおそれがあるため、重要なデータはエクスポートしておいてください。永続化した DB アプリを再起動するときは、Azure ポータルの「再起動」や az containerapp revision restart ではなく、同じ設定で plan_deployment → start_deployment を実行してください（新旧の DB が同じデータを同時に開かないよう、停止 → 更新 → 開始で再起動します））。ローカルの docker-compose（generate_compose）は名前付きボリュームを使うため、再起動してもデータは残ります（docker compose down -v で消えます） |
        | SQL Server は Developer エディション | db_type=sqlserver では MSSQL_PID=Developer で起動します。開発・テスト用途に限られ、本番環境では使えません |
        | SQL Server ＋ Azure ではサンプル DB を作成できない | サンプル DB を使う場合は PostgreSQL（既定）を使ってください。SQL Server では、セットアップウィザードで「空のデータベースを作成する」を選んでください（ローカルの docker-compose では SQL Server でもサンプル DB を作成できます） |
        | Timetracker のメモリ | 3Gi 以上を推奨します |
        | スケールは 1 台固定 | 各コンテナとも 1 台で動作し、冗長化・スケールアウトは行いません |
        | 機密情報の取り扱い | generate_compose で生成する .env には DB パスワードなどが平文で書かれます。共有やリポジトリへのコミットは避けてください。Azure 上ではパスワードを Container Apps の secret として登録します |
        | 利用可能期限 | デプロイには利用可能期限があり、本ツールで作るリソース（3 つの Container App・Container Apps Environment・ストレージアカウント）のタグ（tt-expires-at。UTC）に記録します。plan_deployment の expires_on で、期限日（yyyy-MM-dd。期限日の終わり＝サーバーの時刻帯の 23:59:59）か none（期限なし）を指定できます。省略すると、新しいデプロイは今日から 32 日後、再デプロイは今の期限を引き継ぎます（期限のタグが無い環境は 32 日後）。期限を過ぎると、TTDeployTool のデスクトップアプリが自動で削除します（DB のデータも消えます）。CLI・MCP サーバーは期限での削除をしません |
        | 不要になった環境の削除 | plan_deletion / start_deletion で、本ツールが作ったリソース（Container App と Environment）を削除できます。リソースグループは既定では削除しません。plan_deletion に delete_resource_group=true を指定すると、本ツールのリソースを削除した後にリソースグループが空になる場合だけ、リソースグループも削除します（ほかのリソースが残る場合は削除しません。元に戻せません） |
        """;

    public const string Rbac = """
        # 必要な RBAC ロールと権限

        Azure を操作する ID（サーバーの認証モードの資格情報）には、次のいずれかのロールの割り当てが必要です。

        | ロール（割り当てるスコープ） | できること |
        |---|---|
        | Contributor（サブスクリプション） | すべての操作。リソースグループが無ければ作成する。リソースプロバイダーの登録も自分で行える（通常の推奨） |
        | Contributor（リソースグループ） | 事前に作成したリソースグループへのデプロイと削除。リソースグループの作成とリソースプロバイダーの登録は管理者に依頼する（権限を絞りたい場合の推奨） |
        | Owner（サブスクリプションまたはリソースグループ） | Contributor と同じ |

        **Container Apps Contributor（Container Apps 共同作成者）だけでは使えません。** Microsoft.Resources/subscriptions/resourceGroups/read / write と Microsoft.App/managedEnvironments/write / delete が含まれないためです。

        | 操作 | 必要な権限（Action） |
        |---|---|
        | リソースグループの存在確認 | Microsoft.Resources/subscriptions/resourceGroups/read |
        | リソースグループの作成（無い場合。スコープはサブスクリプション） | Microsoft.Resources/subscriptions/resourceGroups/write |
        | Container Apps Environment の確認・作成 | Microsoft.App/managedEnvironments/read / write |
        | Container App の確認・作成・更新 | Microsoft.App/containerApps/read / write、Microsoft.App/managedEnvironments/join/action |
        | Container App の削除 | Microsoft.App/containerApps/delete |
        | Container Apps Environment の削除 | Microsoft.App/managedEnvironments/delete |
        | DB のデータの永続化（persist_db=true） | Microsoft.Storage/storageAccounts/read / write（作成する場合）/ listkeys/action、Microsoft.Storage/storageAccounts/fileServices/shares/read / write（作成する場合）、Microsoft.App/managedEnvironments/storages/read / write。永続化した DB アプリの更新では Microsoft.App/containerApps/stop/action / start/action |
        | ストレージアカウントの削除（keep_db_data=false） | Microsoft.Storage/storageAccounts/delete |
        | リソースグループの削除（delete_resource_group=true） | Microsoft.Resources/subscriptions/resourceGroups/delete、中のリソースの一覧に Microsoft.Resources/subscriptions/resourceGroups/resources/read |
        | 権限の事前確認 | Microsoft.Authorization/permissions/read（通常どのロールでも可） |

        plan_deployment / plan_deletion は、上の権限があるかを事前に確認し、不足していれば blockers に返します（リソースグループがまだ無い場合などは確認できず、warnings に理由を返します）。拒否割り当てや Azure Policy による拒否は、この確認には反映されません。persist_db=true では、Azure Policy でストレージアカウントのアカウントキーによるアクセス（allowSharedKeyAccess）やパブリック ネットワーク アクセスを禁止していると、DB のマウントが失敗します。

        ## リソースプロバイダーの登録

        本ツールはリソースプロバイダーの登録を行いません。初めて Container Apps を使うサブスクリプションでは、事前に登録してください（サブスクリプションのスコープでの Contributor または Owner が必要）。

        - Microsoft.App（必須）
        - Microsoft.OperationalInsights（必須。Container Apps Environment が使用）
        - Microsoft.Web（任意）
        - Microsoft.Storage（persist_db=true で DB のデータを永続化する場合）

        ```bash
        az provider register --namespace Microsoft.App
        az provider register --namespace Microsoft.OperationalInsights
        az provider register --namespace Microsoft.Storage
        az provider show --namespace Microsoft.App --query "registrationState"
        ```
        """;

    private readonly IServiceProvider _services;

    public TimetrackerResources(IServiceProvider services)
    {
        _services = services;
    }

    [McpServerResource(UriTemplate = ConstraintsUri, Name = "constraints", Title = "本ツールの目的と注意事項", MimeType = "text/markdown")]
    [Description("検証環境向けであること、DB のデータが永続化されないこと、SQL Server の制約、削除の範囲などの注意事項。")]
    public static string GetConstraints() => Constraints;

    [McpServerResource(UriTemplate = RbacUri, Name = "rbac", Title = "必要な RBAC ロールと権限", MimeType = "text/markdown")]
    [Description("Azure を操作する ID に必要なロール・権限と、リソースプロバイダーの登録手順。")]
    public static string GetRbac() => Rbac;

    [McpServerResource(UriTemplate = DeploymentUriTemplate, Name = "deployment", Title = "デプロイ済みの環境の状態", MimeType = "application/json")]
    [Description("get_deployment_info と同じ内容（Environment と各 Container App の状態、管理タグの有無、Timetracker の URL）。")]
    public async Task<string> GetDeployment(string subscription, string resource_group, string app_name, CancellationToken cancellationToken)
    {
        var tools = ActivatorUtilities.CreateInstance<DeploymentJobTools>(_services);
        var info = await tools.GetDeploymentInfo(subscription, resource_group, app_name, cancellationToken: cancellationToken);
        return JsonSerializer.Serialize(info, McpServerHost.JsonOptions);
    }
}
