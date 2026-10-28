# TTDeployTool CLI の構造

- 対象: `feature/desktop-app` の 2026-10-28 時点のコード
- 関連: [共通部分の構造](../common/README.md)（層・ドメインモデル・Azure に作るもの）・[`cli-delete.md`](cli-delete.md)（delete の詳細設計）・リポジトリの README（オプションの一覧）
- 図は Mermaid で書いています（GitHub と、Mermaid に対応したエディターで図として表示されます）。コードの構成を変えたら、この図も直します

CLI（`ttdeploytool-cli`）は、引数を読んで、共通の層の Planner と Runner を直接呼ぶ、いちばん薄い入り口です。計画を保管せず、ジョブも作らず、1 回の実行の中で確かめてから順に実行し、終わったら終了コード（成功 0・失敗 1）を返します。

## 1. プロジェクトの構成

```mermaid
flowchart TB
  subgraph Cli["Controller.Cli"]
    Prog["Program.cs<br/>CliApplication.RunAsync(args)"]
    Args["CliArgs<br/>deploy / delete と --option の解析・ヘルプ"]
    AppC["CliApplication<br/>RunDeployAsync・RunDeleteAsync"]
  end
  subgraph Shared["共通の層"]
    Dom["Domain<br/>DeployOptionsResolver・ComposeGenerator<br/>DeploymentAdvisor・DbPersistence"]
    App["Application<br/>DeploymentPlanner / Runner<br/>DeletionPlanner / Runner"]
    Infra["Infrastructure.Azure<br/>AzureSdkExecutor・CredentialFactory"]
    Common["Common<br/>SimpleLogger（コンソール）"]
  end
  Files[("作業フォルダー<br/>docker-compose.yml・.env")]
  Azure[("Azure")]
  Prog --> AppC
  AppC --> Args
  AppC --> Dom
  AppC --> App
  AppC -- GatewayFactory --> Infra
  AppC --> Common
  App --> Infra
  Infra --> Azure
  AppC -- Dry-run --> Files
```

- Azure の操作は `GatewayFactory`（既定は `AzureSdkExecutor` と、`--auth-mode` で作る資格情報）で作るため、テストでは偽物に差し替えます
- 入出力（標準出力・標準エラー・標準入力・作業フォルダー・時刻）は `RunAsync` の引数で受けるため、テストでコンソールを使わずに確かめられます

## 2. deploy の流れ

```mermaid
flowchart TB
  Start(["ttdeploytool-cli deploy ..."]) --> Parse["CliArgs で解析<br/>必須: --db-password・--tracker-password<br/>（Dry-run でなければ --subscription・--resource-group も）"]
  Parse --> Resolve["DeployInput → DeployOptionsResolver<br/>既定値を当てて DeployOptions に"]
  Resolve --> Name{"アプリ名は正しいか"}
  Name -- いいえ --> Fail(["終了コード 1"])
  Name -- はい --> Dry{"--dry-run"}
  Dry -- はい --> Compose["ComposeGenerator で検証<br/>docker-compose.yml と .env を作業フォルダーに書く"]
  Compose --> Ok(["終了コード 0"])
  Dry -- いいえ --> Validate["検証（ComposeGenerator.Validate）"]
  Validate --> Perm["DeploymentPlanner.CheckPermissionsAsync<br/>必要な権限があるか"]
  Perm -- 足りない --> Fail
  Perm -- ある --> Run["DeploymentRunner.RunAsync<br/>リソースグループ → Environment →<br/>（永続化なら）ストレージ → 3 つの Container App"]
  Run --> Notice["URL・ストレージアカウント・利用可能期限を表示"]
  Notice --> Ok
```

## 3. delete の流れ

```mermaid
flowchart TB
  Start(["ttdeploytool-cli delete ..."]) --> Plan["DeletionPlanner.PlanAsync<br/>管理タグで消すものを決める<br/>--include-untagged・--keep-db-data・--delete-resource-group"]
  Plan --> Show["計画を表示"]
  Show --> Dry{"--dry-run"}
  Dry -- はい --> Ok(["終了コード 0"])
  Dry -- いいえ --> Block{"リソースグループを消せない理由がある"}
  Block -- はい --> Fail(["終了コード 1"])
  Block -- いいえ --> Any{"消すものがある"}
  Any -- いいえ --> Ok
  Any -- はい --> Perm{"権限がある"}
  Perm -- いいえ --> Fail
  Perm -- はい --> Yes{"--yes"}
  Yes -- いいえ --> Ask["アプリ名の入力を求める"]
  Ask -- 一致しない --> Fail
  Ask -- 一致 --> Run
  Yes -- はい --> Run["DeletionRunner.RunAsync<br/>Container App → Environment → ストレージアカウント"]
  Run --> RG{"リソースグループも消す計画"}
  RG -- はい --> DelRG["DeleteResourceGroupAsync"] --> Ok
  RG -- いいえ --> Ok
```

## 4. MCP サーバー・デスクトップアプリとの違い

- 計画の保管（`IPlanStore`）とジョブ（`JobManager`）を使いません。計画を作ってから実行までが 1 回の実行の中で続くため、「実行の直前に計画し直して比べる」処理は要りません
- 確認は、計画をコンソールに表示し、アプリ名を入力してもらう方法です（CI などでは `--yes` で省きます）
- Azure の資格情報は `--auth-mode`（`default`・`azure-cli`・`sp-env`・`device-code`・`managed-identity`。既定は `DefaultAzureCredential`）で選びます
