# TTDeployTool 共通部分の構造

- 対象: `feature/desktop-app` の 2026-10-28 時点のコード
- 関連: [CLI の構造](../cli/README.md)・[MCP サーバーの構造](../mcp/README.md)・[デスクトップの構造](../desktop/README.md)・[`MCP_TOOL_DESIGN.md`](../../MCP_TOOL_DESIGN.md)（全体設計。3.2 依存関係・3.4 Application を共有する方針）・[`db-persistence.md`](db-persistence.md)（DB のデータの永続化）・[`resource-group-deletion.md`](resource-group-deletion.md)（リソースグループの削除）
- 図は Mermaid で書いています（GitHub と、Mermaid に対応したエディターで図として表示されます）。コードの構成を変えたら、この図も直します

TTDeployTool には、利用者の入り口が 3 つ（CLI・MCP サーバー・デスクトップアプリ）あり、どれも同じ Domain・Application・Infrastructure.Azure を使います。入り口が違っても、同じ入力からは同じリソースを、同じ名前と管理タグで作ります。

## 1. 全体の構成と依存の向き

矢印は「使う」向きです。入り口（Controller）は利用者とのやり取りだけを持ち、検証・計画・実行は共通の層に置きます。

```mermaid
flowchart TB
  subgraph Entry["入り口（Controller）"]
    CLI["Controller.Cli<br/>ttdeploytool-cli deploy / delete"]
    MCP["Controller.Mcp<br/>AI アシスタント向け（stdio・HTTP）"]
    Desk["Controller.Desktop + Desktop.Core<br/>Windows のアプリ（WinUI 3）"]
  end
  subgraph App["Application"]
    WF["Workflows<br/>DeploymentWorkflow・DeletionWorkflow<br/>計画 → 確認 → 実行"]
    PR["Deployment・Deletion<br/>Planner（計画）・Runner（実行）<br/>DeploymentInspector（状態）"]
    Jobs["Jobs<br/>JobManager・IPlanStore・IJobStore"]
    Inv["Inventory<br/>DeploymentCatalog（一覧）<br/>DeploymentMaintenance（期限の変更・パスワード）"]
    Perm["Permissions<br/>PermissionChecker"]
    Comp["Compose<br/>ComposeExporter（Dry-run）"]
  end
  Dom["Domain<br/>DeployInput → DeployOptions<br/>検証・既定値・名前・管理タグ<br/>利用可能期限・権限の評価・Compose の生成"]
  Infra["Infrastructure.Azure<br/>IAzureDeploymentGateway（AzureSdkExecutor）<br/>ContainerAppSpecBuilder・CredentialFactory<br/>Resource Graph・テナント・サブスクリプション"]
  Common["Common<br/>IDeploymentLogger"]
  Azure[("Azure<br/>ARM・Container Apps・Storage<br/>Resource Graph・Entra ID")]
  CLI --> PR
  CLI --> Comp
  MCP --> WF
  MCP --> Inv
  Desk --> WF
  Desk --> Inv
  WF --> PR
  WF --> Jobs
  PR --> Perm
  PR --> Dom
  PR --> Infra
  Inv --> Infra
  Comp --> Dom
  Perm --> Dom
  Infra --> Dom
  Infra --> Common
  App --> Common
  Infra --> Azure
```

| プロジェクト | 役割 | 持たないもの |
|---|---|---|
| `TTDeployTool.Domain` | 入力の既定値と検証、リソースの名前（`ResourceNaming`）、管理タグ（`ManagedTags`）、利用可能期限（`DeploymentExpiry`）、権限の評価（`PermissionEvaluator`）、docker-compose.yml と .env の生成（`ComposeGenerator`） | Azure の SDK・入出力 |
| `TTDeployTool.Application` | 計画（何を作る・変える・消すか、ブロッカーと警告、権限の確認）と実行、計画 → 確認 → 実行の流れ（`Workflows`）、計画とジョブの保管（`IPlanStore`・`JobManager`）、デプロイの一覧 | 画面・引数・トランスポート |
| `TTDeployTool.Infrastructure.Azure` | Azure の操作の窓口 `IAzureDeploymentGateway` とその実装（`AzureSdkExecutor`）、Container App の定義（`ContainerAppSpecBuilder`）、資格情報（`CredentialFactory`）、Resource Graph での一覧の読み取り | 計画の判断 |
| `TTDeployTool.Common` | ログの窓口（`IDeploymentLogger`）と、コンソール向けの実装 | — |

補足: Azure の窓口のインターフェース（`IAzureDeploymentGateway`）は Infrastructure.Azure の側にあり、Application は Infrastructure.Azure を参照しています（依存はすべて内向きではありません）。テストでは、この窓口を偽物に差し替えます。

## 2. 計画 → 確認 → 実行

MCP サーバーとデスクトップアプリは `Workflows` を通して、計画を保管し、利用者が確かめた後に、実行の直前に計画を作り直して比べてから始めます。CLI は Planner と Runner を直接呼び、コンソールで確かめます（[CLI の構造](../cli/README.md)）。

```mermaid
sequenceDiagram
  participant C as 入り口（MCP・デスクトップ）
  participant WF as DeploymentWorkflow / DeletionWorkflow
  participant P as Planner
  participant G as IAzureDeploymentGateway
  participant S as IPlanStore
  participant J as JobManager
  participant R as Runner
  C->>WF: PlanAsync（入力）
  WF->>P: 計画を作る
  P->>G: 既存のリソース・タグ・権限を読む
  P-->>WF: 計画（操作・ブロッカー・警告・権限の確認）
  WF->>S: 計画を保管（有効期限つき。呼び出し元ごと）
  WF-->>C: 計画とその ID
  Note over C: 利用者が計画を確かめる（データ消失の同意・アプリ名の入力）
  C->>WF: StartAsync（計画の ID）
  WF->>S: 計画を取り出す
  WF->>P: 計画し直して比べる（変わっていれば始めない）
  WF->>J: ジョブを登録（同じ対象は同時に 1 つ）
  J->>R: バックグラウンドで手順を順に実行
  R->>G: 作成・更新・削除
  C->>J: Snapshot（状態・手順・ログ・結果）
```

## 3. ドメインモデル

利用者の入力 `DeployInput` は、既定値と検証を経て `DeployOptions` になります。計画（`DeploymentPlan`・`DeletionPlan`）は実行の前に利用者が確かめる単位で、ジョブ（`Job`）はその実行です。リソースの名前は `ResourceNaming`（`{app}-env`・`-db`・`-redis`・`-tt`）、本ツールで作ったものかは管理タグ `managed-by=ttdeploytool` で決めます。

```mermaid
classDiagram
  direction LR
  class DeployInput {
    Subscription・ResourceGroup・AppName
    DbType・パスワード・CPU とメモリ
    PersistDb・ExpiresOn
  }
  class DeployOptions {
    Subscription・ResourceGroup・Location
    AppName・DbType・DbName
    TimetrackerTag・CPU とメモリ
    PersistDb・ExpiresOn
  }
  class DeploymentPlan {
    Operations
    Blockers・Warnings
    CanRun
    データ消失の同意が要るか
  }
  class PlannedOperation {
    Kind（リソースの種類）
    Name
    Action（Create・UseExisting・Update）
    DataLoss
  }
  class PermissionCheckResult {
    Status
    MissingActions
  }
  class ExpiryDecision {
    ExpiresAt
    Source（指定・既定・なし）
  }
  class DeploymentResult {
    EnvironmentName
    3 つの Container App の名前
    TimetrackerUrl
    StorageAccountName
  }
  class DeletionPlan {
    Subscription・ResourceGroup・AppName
    IncludeUntagged・KeepDbData
    HasDeletions
  }
  class DeletionTarget {
    Name・Kind
    Exists・Managed・WillDelete
  }
  class ResourceGroupDeletion {
    WillDelete
    Remaining
  }
  class DeletionOutcome {
    Deleted・AlreadyDeleted
    ResourceGroupDeleted
  }
  class DeploymentEntry {
    デプロイの一覧の 1 件
    Url・DeployedAt
    ExpiryKind・ExpiresAt
    DbPersisted
  }
  class AppEntry {
    Name
    ProvisioningState・RunningStatus
    Image・Cpu・Memory
  }
  class Job {
    RequestStop()
    Snapshot()
  }
  class JobSnapshot {
    Kind（Deploy・Delete）
    Status（Running・Succeeded・Failed・Stopped）
    CurrentStep・Logs
    Result・Error
  }
  class JobTarget {
    Subscription
    ResourceGroup
    AppName
  }
  DeployInput ..> DeployOptions : DeployOptionsResolver
  DeploymentPlan *-- DeployOptions
  DeploymentPlan *-- "1..*" PlannedOperation
  DeploymentPlan *-- PermissionCheckResult
  DeploymentPlan o-- ExpiryDecision
  DeploymentResult o-- ExpiryDecision
  DeletionPlan *-- "1..*" DeletionTarget
  DeletionPlan o-- ResourceGroupDeletion
  DeletionPlan *-- PermissionCheckResult
  DeploymentEntry *-- "0..3" AppEntry : tt・db・redis
  Job --> JobSnapshot : Snapshot()
  JobSnapshot *-- JobTarget
  JobSnapshot ..> DeploymentResult : Result
  JobSnapshot ..> DeletionOutcome : Result
```

## 4. Azure に作るもの

1 つのデプロイ（アプリ名 `{app}`）は、1 つのリソースグループの中の次のリソースです。DB と Redis は Environment の中だけで TCP で受け、Timetracker だけを外に公開します。`--persist-db`（永続化）を選ぶと、DB のデータをストレージアカウントのファイル共有に置きます（[`db-persistence.md`](db-persistence.md)）。

```mermaid
flowchart LR
  subgraph RG["リソースグループ"]
    subgraph Env["Container Apps Environment<br/>{app}-env"]
      TT["Container App {app}-tt<br/>Timetracker<br/>外部公開（HTTPS）"]
      DB["Container App {app}-db<br/>PostgreSQL 5432 / SQL Server 1433<br/>内部（TCP）"]
      Redis["Container App {app}-redis<br/>Redis 6379<br/>内部（TCP）"]
      EnvSt["Environment のストレージ<br/>（永続化するときだけ）"]
    end
    SA["ストレージアカウント tt…<br/>ファイル共有 {app}-db<br/>（永続化するときだけ）"]
  end
  User(("利用者のブラウザー")) --> TT
  TT --> DB
  TT --> Redis
  DB -. マウント .-> EnvSt
  EnvSt -. Azure Files .-> SA
```

| 管理タグ | 値 | 付けるもの | 使い道 |
|---|---|---|---|
| `managed-by` | `ttdeploytool` | 作ったリソースすべてと、デプロイのときに作ったリソースグループ | 本ツールで作ったものかの判断。削除は、既定でこのタグと `tt-app-name` のあるものだけを消す（`--include-untagged` で無いものも）。リソースグループは、このタグがあるときだけ消せる |
| `tt-app-name` | アプリ名 | 作ったリソースすべて（リソースグループには付けない。複数のアプリ名で共有できるため） | どのデプロイのリソースかの判断（デプロイの一覧で束ねる） |
| `tt-deployed-at` | デプロイした日時（UTC） | 3 つの Container App・Environment・ストレージアカウント | デプロイの一覧の表示 |
| `tt-expires-at` | 利用可能期限（UTC）か `none` | 3 つの Container App・Environment・ストレージアカウント | 期限の前の通知と、期限での削除（デスクトップアプリ） |

削除は、本ツールで作ったもの（管理タグのあるもの）だけを消し、リソースグループは既定では残します。リソースグループも消すのは、利用者が選び、本ツールが作ったもので、消した後に空になる場合だけです（[`resource-group-deletion.md`](resource-group-deletion.md)）。

## 5. 入り口ごとの違い

| 項目 | CLI | MCP サーバー | デスクトップアプリ |
|---|---|---|---|
| 使う人 | コマンドを使う開発者・CI | AI アシスタントの利用者 | CLI・AI を使わない利用者 |
| 確認の方法 | 計画を表示して、アプリ名を入力（`--yes` で省略） | 計画の ID を返し、利用者が確かめてから `start_*` | 画面で計画を見て、同意・アプリ名を入力 |
| 実行 | その場で順に実行（終わるまで待つ） | ジョブ（`get_job_status` で状態を読む） | ジョブ（同時に 2 つまで・予定・停止。アプリを終了しても予定と記録を残す） |
| Azure の資格情報 | `--auth-mode`（既定は DefaultAzureCredential） | stdio は PC の資格情報、HTTP はサーバーの ID か利用者本人（OBO） | WAM で Entra ID にサインイン |
| 構造の文書 | [cli/README.md](../cli/README.md) | [mcp/README.md](../mcp/README.md) | [desktop/README.md](../desktop/README.md) |
