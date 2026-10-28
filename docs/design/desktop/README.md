# TTDeployTool デスクトップの構造

- 対象: `feature/desktop-app` の 2026-10-27 時点のコード（REVIEW_ISSUES No.76 まで）
- 関連: [共通部分の構造](../common/README.md)（層・ドメインモデル・Azure に作るもの）・[`desktop-app.md`](desktop-app.md)（詳細設計）・[`desktop-jobs.md`](desktop-jobs.md)（ジョブ・予定・記録）・[`desktop-wizard.md`](desktop-wizard.md)（画面の流れ）
- 図は Mermaid で書いています（GitHub と、Mermaid に対応したエディターで図として表示されます）。コードの構成を変えたら、この図も直します

WinUI 3 の画面は薄く保ち、画面の動きは画面に依らない `Desktop.Core` に置いて単体テストで確かめます。デプロイと削除の計画・実行は、CLI と MCP サーバーと同じ `Application` 層を使います。

## 1. プロジェクトの構成と依存の向き

矢印は「使う」向きです。`Desktop` は Windows に固有の部分（サインイン・通知・暗号化など）だけを持ち、`Desktop.Core` が決めた窓口（インターフェース）を実装します。`Desktop.Core` の参照は `Application` だけで、Azure の型は `Application` を通して使います。

```mermaid
flowchart TB
  subgraph Win["Windows"]
    WAM["WAM<br/>Entra ID のサインイン"]
    Toast["通知・StartupTask<br/>サインイン時の起動"]
    Files[("アプリのデータ<br/>preferences.json<br/>job-history.json・logs")]
  end
  subgraph Desktop["Controller.Desktop（WinUI 3・MSIX）"]
    UI["MainWindow.xaml<br/>画面と x:Bind"]
    Impl["Windows の実装<br/>WamSignInService・DialogService<br/>AppNotificationService・StartupTaskService<br/>DpapiSecretProtector・ClipboardService"]
  end
  subgraph Core["Controller.Desktop.Core（画面に依らない .NET 10）"]
    VM["ViewModels<br/>MainViewModel と画面ごとの ViewModel"]
    Jobs["Jobs<br/>JobQueue・ExpiryWatcher<br/>ScheduledJobStore・JobHistory"]
    Ports["Services（窓口）<br/>ISignInService・IAzureClients・IDialogService<br/>INotificationService・ISecretProtector<br/>IAppPreferences・IJobHistoryFile"]
  end
  subgraph App["Application"]
    WF["Workflows<br/>DeploymentWorkflow・DeletionWorkflow"]
    Plan["計画と実行<br/>DeploymentPlanner / Runner<br/>DeletionPlanner / Runner"]
    JM["Jobs<br/>JobManager・InMemoryPlanStore"]
    Inv["Inventory<br/>DeploymentCatalog・DeploymentMaintenance"]
  end
  Dom["Domain<br/>入力と既定値・名前・管理タグ<br/>利用可能期限・権限の規則"]
  Infra["Infrastructure.Azure<br/>IAzureDeploymentGateway（AzureSdkExecutor）<br/>Resource Graph・テナント・サブスクリプション"]
  Common["Common<br/>IDeploymentLogger"]
  CLI["Controller.Cli"]
  MCP["Controller.Mcp"]
  Azure[("Azure<br/>ARM・Container Apps・Resource Graph")]

  UI --> VM
  Impl -. 実装 .-> Ports
  Impl --> WAM
  Impl --> Toast
  VM --> Jobs
  VM --> Ports
  Jobs --> Ports
  Ports -- FilePreferences・FileJobHistory --> Files
  VM --> WF
  VM --> Inv
  Jobs --> JM
  WF --> Plan
  WF --> JM
  Plan --> Dom
  Plan --> Infra
  Inv --> Infra
  Infra --> Dom
  Infra --> Common
  App --> Common
  Infra --> Azure
  CLI --> App
  MCP --> App
```

凡例: 実線: 使う（参照・呼び出し） / 点線: 窓口を実装する / 円柱: アプリの外にあるもの

## 2. Desktop.Core の中: 画面の ViewModel とジョブ

`MainViewModel` が画面全体を組み立てます。デプロイ・削除・まとめての削除・期限での削除は、どれも `JobQueue` にジョブとして登録し、同時に実行する数（既定 2）・予定の時刻・同じ対象の重複を `JobQueue` がまとめて扱います。

```mermaid
flowchart LR
  Main["MainViewModel"]
  subgraph Screens["画面ごとの ViewModel"]
    SignIn["SignInViewModel"]
    Target["TargetViewModel<br/>サブスクリプション・対象"]
    Settings["SettingsViewModel<br/>デプロイの設定・テンプレート"]
    Wizard["DeployWizardViewModel<br/>① 〜 ⑤ と完了の表示"]
    Deploy["DeploymentViewModel<br/>計画・実行・予定"]
    Delete["DeletionViewModel"]
    Batch["BatchDeletionViewModel"]
    List["DeploymentListViewModel"]
    Row["DeploymentRowViewModel"]
    Status["StatusViewModel<br/>実行状況"]
    JobList["JobListViewModel<br/>並べ替え・絞り込み・記録から削除"]
    Sys["SystemStatusViewModel<br/>Windows との連携"]
    Dry["DryRunViewModel"]
  end
  subgraph JobArea["ジョブ"]
    Queue["JobQueue"]
    JobVM["JobViewModel<br/>状態・日時・ログ・結果"]
    Expiry["ExpiryWatcher<br/>期限の前の通知・期限での削除"]
    Store["ScheduledJobStore<br/>予定を残して戻す"]
    History["JobHistory<br/>終わったジョブの記録"]
  end
  Main --> Screens
  Wizard --> Settings
  Wizard --> Deploy
  List --> Row
  Deploy -- Register --> Queue
  Delete -- Register --> Queue
  Batch -- Register --> Queue
  Expiry -- 期限での削除 --> Queue
  Expiry --> List
  Queue --> JobVM
  JobList --> Queue
  Status --> JobVM
  Row -. 同じ対象のジョブ .-> Queue
  Store <--> Queue
  History <--> Queue
  Sys --> Store
```

## 3. 予定に入れたデプロイが実行されるまで

計画 → 確認 → 実行の順に進み、予定の時刻が来たら、実行の直前に計画を作り直して、確かめた計画と同じときだけ始めます。予定はパスワードを DPAPI で暗号化して残すため、アプリを終了しても次の起動で戻ります。

```mermaid
sequenceDiagram
  actor U as 利用者
  participant VM as DeploymentViewModel
  participant WF as DeploymentWorkflow
  participant Az as Azure
  participant Q as JobQueue
  participant S as ScheduledJobStore
  participant JM as JobManager
  participant H as JobHistory
  U->>VM: ⑤「計画」
  VM->>WF: PlanAsync（DeployOptions）
  WF->>Az: 既存のリソースと権限を読む
  WF-->>VM: DeploymentPlanTicket（操作・ブロッカー・警告）
  U->>VM: 「予定に追加」（データ消失の同意）
  VM->>Q: Register（予定の時刻・始める処理）
  Q->>S: 保存（パスワードは DPAPI で暗号化）
  Note over Q: 15 秒ごとに予定の時刻を確かめる
  Q->>VM: 始める処理（実行の直前の確認）
  VM->>WF: 計画し直して比べる
  WF->>JM: StartAsync
  JM->>Az: DeploymentRunner が手順を順に実行
  loop 1 秒ごと
    Q->>JM: Snapshot（状態・手順・ログ）
  end
  Q->>H: 終わったら記録（job-history.json）
  Q-->>U: 通知（完了・失敗）
```

## 4. ドメインモデル

デプロイ・削除の計画と結果、ジョブ、デプロイの一覧の型は、CLI・MCP サーバーと共通です。[共通部分の構造](../common/README.md)の 3 にあります。この文書では、デスクトップに固有のジョブの状態（5）と PC に残すデータ（6）を示します。

## 5. デスクトップのジョブの状態

始めるまでは `JobQueue` が状態を持ち、始めた後は Application の `Job` の状態を映します。終わった状態（完了・失敗・停止・取り消し）になったジョブは、記録として残ります。

```mermaid
stateDiagram-v2
  [*] --> Scheduled : 予定に追加
  [*] --> Waiting : 今すぐ実行
  Scheduled --> Starting : 予定の時刻（空きがある）
  Waiting --> Starting : 同時に実行できる数に空きがある
  Scheduled --> Paused : 一時停止
  Waiting --> Paused : 一時停止
  Paused --> Scheduled : 再開（時刻の前）
  Paused --> Waiting : 再開（時刻を過ぎた）
  Scheduled --> Canceled : 取り消し
  Waiting --> Canceled : 取り消し
  Paused --> Canceled : 取り消し
  Starting --> Running : 計画が同じ
  Starting --> Failed : 計画が変わった・始められない
  Running --> Stopping : 停止
  Stopping --> Stopped : 今の手順の後で止める
  Running --> Succeeded
  Running --> Failed
  Succeeded --> [*] : 記録に残す
  Failed --> [*] : 記録に残す
  Stopped --> [*] : 記録に残す
  Canceled --> [*] : 記録に残す
```

## 6. この PC に残すデータ

Azure の状態は Azure のタグと Resource Graph が正で、PC には利用者の設定・予定・記録だけを残します。パスワードは、予定を残すと選んだデプロイの予定だけ、DPAPI で暗号化して残します。

```mermaid
classDiagram
  direction LR
  class JobViewModel {
    State・ScheduledAt
    RegisteredAt・ChangedAt
    StartedAt・FinishedAt
    Logs・Error・Result
  }
  class SavedJob {
    対象・予定の時刻・一時停止か
    アカウント
    RegisteredAt・ChangedAt
  }
  class SavedDeletion {
    確かめた計画の選択と中身
    確かめたアプリ名
  }
  class SavedDeployment {
    パスワードを空にした設定
    暗号化したパスワード（DPAPI）
    確かめた計画の操作
  }
  class JobRecord {
    対象・状態・各日時
    結果・エラー・注意・ログ
  }
  class IAppPreferences {
    preferences.json
    同時実行の数・テンプレート・予定
  }
  class IJobHistoryFile {
    job-history.json
    180 日・500 件まで
  }
  JobViewModel ..> SavedJob : ScheduledJobStore（まだ始めていないもの）
  SavedJob o-- SavedDeletion
  SavedJob o-- SavedDeployment
  JobViewModel ..> JobRecord : JobHistory（終わったもの）
  IAppPreferences *-- SavedJob
  IJobHistoryFile *-- JobRecord
```

| ファイル | 中身 | 消えるとき |
|---|---|---|
| （フォルダー） | パッケージ版は `%LOCALAPPDATA%\Packages\TTDeployTool_<発行元のハッシュ>\LocalState`、パッケージにしない場合は `%LOCALAPPDATA%\TTDeployTool`（[Windows での確認手順](desktop-app-check.md)の 2.4） | アンインストール |
| `preferences.json` | 同時に実行する数・テンプレート（パスワードなし）・出した期限の通知・まだ始めていない予定・前回のサインイン時の起動 | 予定は始めたとき・取り消したとき |
| `job-history.json` | 終わったジョブの記録（結果・ログ・日時。パスワードなし） | 180 日・500 件を超えたとき、「記録から削除」 |
| `logs/` | 診断用のログ（パスワード・トークン・アカウント名なし） | 7 日 |
| `authentication-record.json` | 前回サインインしたアカウントとテナントの識別子（AuthenticationRecord。秘密の情報なし）。トークンのキャッシュは、このフォルダーの外に DPAPI で守って置く（`desktop-app.md` の 11 の 2） | サインアウト（トークンのキャッシュは有効期限まで残る） |
