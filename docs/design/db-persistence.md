# 詳細設計: DB データの永続化オプション

- 対象ブランチ: `claude/db-persistence`
- 将来の検討: `docs/ROADMAP.md`（1. DB データの永続化オプション）
- 関連: `docs/REVIEW_ISSUES.md` No.4（永続化しない仕様にしたオーナー判断）、No.19（Azure 上の SQL Server でサンプル DB を作れない制約）、No.40（再デプロイの警告の文言）
- 既存の設計: `docs/design/cli-delete.md`（管理タグ・削除の規則・権限の事前確認）、`docs/MCP_TOOL_DESIGN.md`（4.7 `plan_deployment` / `start_deployment`、4.12 削除、4.13 必要な Azure 権限）

> [!NOTE]
> 13 の質問（Q1〜Q4）は、オーナーの回答で決定した（Q1: SMB、Q2: 削除では既定でデータも消す、Q3: 指定が無ければ今の状態を引き継ぐ、Q4: No.19 は別の項目にする）。

## 1. 目的とスコープ
Azure 上の DB のデータを、DB コンテナの再起動（設定を変えた再デプロイ、リビジョンの更新、Azure 基盤のメンテナンス）の後も残せるようにする。**既定は今の非永続のまま**にし、指定したときだけ永続化する（No.4 のオーナー判断「検証環境の構築支援が目的のため、永続化しない仕様」は既定の動きとして維持する）。

| 項目 | このブランチで行う |
|---|---|
| CLI の `--persist-db`、MCP の `persist_db` で、DB のデータを Azure Files に置く | ✔ |
| ストレージアカウント・ファイル共有の作成、Environment へのストレージの登録、DB コンテナへのマウント | ✔ |
| 計画（`plan_deployment`）・権限の事前確認・再デプロイの警告と `acknowledge_data_loss` の判定の更新 | ✔ |
| 削除（`delete` / `plan_deletion`）でのストレージの扱い | ✔ |
| `get_deployment_info` に永続化の状態を出す | ✔ |
| README（目的と注意事項・作成されるリソース・RBAC・料金・FAQ）、MCP の Resources / instructions の更新 | ✔ |
| Azure 上の SQL Server でのサンプル DB の作成（No.19） | ✘（【決定 Q4】別の項目にする。永続化を Azure で確かめてから、同じストレージの仕組みで行う） |
| Redis の永続化 | ✘（キャッシュとジョブのキューで、消えても Timetracker のデータは失われないため） |
| バックアップ・リストア、冗長化（ZRS / GRS） | ✘（検証環境向けの方針のまま。LRS に固定） |
| Dry-run（docker-compose） | ✘（既に名前付きボリュームで永続化している。`--persist-db` は Azure 向けの設定のため、Dry-run では警告を出して無視する） |

## 2. 一次情報で確かめたこと
| 項目 | 内容 | 出典 |
|---|---|---|
| Container Apps のストレージ | Azure Files を SMB（`AzureFile`）または NFS（`NfsAzureFile`）でマウントできる。Environment にストレージ（`managedEnvironments/storages`）を登録し、リビジョンの `volumes` と、コンテナの `volumeMounts`（`mountPath` / `subPath`）で使う。`mountOptions` を指定できる。従来型のファイル共有（`Microsoft.Storage/storageAccounts/fileServices/shares`）だけが使える | [Use storage mounts in Azure Container Apps](https://learn.microsoft.com/azure/container-apps/storage-mounts) |
| SMB の認証 | Environment のストレージに、ストレージアカウント名と**アカウントキー**を登録する（SDK には Key Vault 参照のプロパティもあるが、Environment のマネージド ID が要る） | 同上 |
| NFS の前提 | **カスタム VNet の Environment が必須**。ストレージアカウントは VNet からのアクセスを許可し、NFS の転送中の暗号化を必須にしない。NSG で 445 / 2049 を許可。NFS は Premium（FileStorage）のファイル共有だけ | 同上、[NFS file shares in Azure Files](https://learn.microsoft.com/azure/storage/files/files-nfs-protocol) |
| リビジョンの更新 | 単一リビジョンモードでも、**新しいリビジョンが準備できるまで古いリビジョンは止まらない**（短い間、新旧のコンテナが同時に動く） | [Revisions in Azure Container Apps](https://learn.microsoft.com/azure/container-apps/revisions) |
| SQL Server と SMB | SQL Server on Linux は、SMB 3.0 以降の共有をデータのフォルダーにマウントして使える（`uid` / `gid` を `mssql` ユーザーにする手順が公式にある） | [Configure SMB storage for an FCI - SQL Server on Linux](https://learn.microsoft.com/sql/linux/sql-server-linux-shared-disk-cluster-configure-smb) |
| PostgreSQL と SMB | AKS の Azure Files（SMB）では、マウント後に権限を変えられないため `initdb: could not change permissions of directory` で失敗する（回避策は Azure Disk）。PostgreSQL の文書は NFS（`hard` マウント）には触れているが SMB には触れていない。データのディレクトリは `0700`（または `0750`）で、PostgreSQL のユーザーが所有している必要がある | [Could not change permissions using Azure Files](https://learn.microsoft.com/troubleshoot/azure/azure-kubernetes/storage/could-not-change-permissions-azure-files)、[PostgreSQL 16: Creating a Database Cluster](https://www.postgresql.org/docs/16/creating-cluster.html) |
| SMB の mountOptions | `uid` / `gid` / `dir_mode` / `file_mode` で所有者と権限を固定できる。`mfsymlinks`（シンボリックリンク）、`nobrl`（バイト範囲ロックを送らない）など | [Recommended mountOptions settings on Azure Files](https://learn.microsoft.com/troubleshoot/azure/azure-kubernetes/storage/mountoptions-settings-azure-files) |
| 必要な権限（Action） | `Microsoft.App/managedEnvironments/storages/read` / `write` / `delete`、`Microsoft.Storage/storageAccounts/read` / `write` / `delete` / `listkeys/action`、`Microsoft.Storage/storageAccounts/fileServices/shares/read` / `write` / `delete` | [Azure permissions for Compute](https://learn.microsoft.com/azure/role-based-access-control/permissions/compute)、[Azure permissions for Storage](https://learn.microsoft.com/azure/role-based-access-control/permissions/storage) |
| SDK | `Azure.ResourceManager.AppContainers` 1.6.0（使用中）に `ContainerAppManagedEnvironmentStorage*`、`ContainerAppAzureFileProperties`、`ContainerAppVolume`（`MountOptions`）、`ContainerAppVolumeMount`（`SubPath`）がある。ストレージアカウントとファイル共有の作成には `Azure.ResourceManager.Storage` を追加する | パッケージの XML ドキュメント |

## 3. 方式の選択（SMB か NFS か）【決定 Q1: SMB】
| | SMB（推奨） | NFS |
|---|---|---|
| Environment | 今のまま（VNet なし）。既存の Environment にもストレージを追加できる | **カスタム VNet が必須**。本ツールは VNet を作っておらず、既存の Environment を VNet 付きに変えることはできないため、VNet・サブネット・NSG の作成と、Environment の作り直し（＝既存の環境では使えない）が要る |
| ストレージ | Standard（StorageV2、LRS）。従量課金で小さく始められる | Premium（FileStorage）。プロビジョニング課金（最小 100 GiB） |
| 認証 | アカウントキー（Environment のストレージに登録） | ネットワーク（VNet）で制御。キーは不要 |
| SQL Server | 公式の手順あり（SMB 3.0 以降） | NFS 4.2 以降が必要。Azure Files の NFS は 4.1 のため**公式の要件を満たさない** |
| PostgreSQL | 公式の記載なし。権限の制約があり、`mountOptions`（`uid=999,gid=999,dir_mode=0700,file_mode=0600`）と、マウント先の下のサブディレクトリを `PGDATA` にすることで動く見込み（**Azure での確認が必須**） | 公式の記載あり（`hard` マウント） |
| 変更の大きさ | 中（ストレージ 3 種類の作成・登録とマウント） | 大（ネットワークの設計・作成、権限、削除も増える） |

決定: **SMB**。NFS は VNet の設計が必要になり、「検証環境を手早く作る」目的から外れる。また、SQL Server は NFS の公式の要件（4.2 以降）を満たさない。PostgreSQL は SMB での動作を Azure で確認し、動かない場合は PostgreSQL の永続化を見送る（`--persist-db` を SQL Server だけで使えるようにし、PostgreSQL ではエラーにする）。

## 4. 作成するリソース
`--persist-db` を指定したときだけ、次を追加で作る（既にあれば使う）。名前は `ResourceNaming` に集約する。

| リソース | 名前 | 補足 |
|---|---|---|
| ストレージアカウント | `tt` ＋ アプリ名から英数字以外を除いたもの（先頭 14 文字まで）＋ サブスクリプション ID・リソースグループ名・アプリ名から作るハッシュ（16 進 8 文字）。例: `tttimetracker1a2b3c4d` | 名前は Azure 全体で一意・英小文字と数字の 3〜24 文字のため。同じ入力からは常に同じ名前になる。StorageV2 / Standard_LRS / ホット、`minimumTlsVersion=TLS1_2`、`allowBlobPublicAccess=false`、`supportsHttpsTrafficOnly=true`。管理タグを付ける |
| ファイル共有 | `{アプリ名}-db` | SMB、クォータ 100 GiB（従量課金のため、クォータは上限で料金には関係しない） |
| Environment のストレージ | `dbdata` | `AzureFile`、`ReadWrite`。アカウントキーはデプロイのたびに `listKeys` で取得して登録し直す（キーの再生成に追随するため）。キーはログ・結果に出さない |

- DB アプリ（`{app}-db`）のテンプレートに、ボリューム `dbdata`（`AzureFile`、`storageName: dbdata`、`mountOptions`）と、コンテナのマウントを加える
  - PostgreSQL: `/var/lib/postgresql/data` にマウントし、環境変数 `PGDATA=/var/lib/postgresql/data/pgdata`（PostgreSQL の文書の「マウント先の下に、PostgreSQL のユーザーが所有するディレクトリを作る」に従う）。`mountOptions`: `uid=999,gid=999,dir_mode=0700,file_mode=0600`（`postgres:16` の postgres ユーザー）
  - SQL Server: `/var/opt/mssql` にマウント。`mountOptions`: `uid=10001,gid=0,dir_mode=0770,file_mode=0660`（`mssql/server:2022` の mssql ユーザー。gid は Azure で確認する）
  - 共通: `nobrl` などの追加のオプションは、Azure での確認の結果で決める（9 の確認項目）
- 既存の Environment（本ツールが作ったものでないものを含む）にもストレージを追加する。Environment 自体は従来どおり PUT しない（子リソースの `storages` だけを作成・更新する）。この点は計画の表示で明示する

### 4.1 リビジョンの更新で新旧のコンテナが同時に動く問題
2 のとおり、更新の間は古いリビジョンと新しいリビジョンが短い間同時に動く。同じデータのファイルを 2 つの DB のプロセスが開くと、データが壊れるおそれがある（PostgreSQL の `postmaster.pid` による排他は、別のコンテナでは PID が同じ 1 になりうるため、確実には働かない）。

- 対策: **永続化した既存の DB アプリを更新するときは、先に DB アプリを停止（`stop`）し、更新してから開始（`start`）する**。停止の間（数十秒〜数分）は Timetracker から DB に接続できない
- 必要な権限: `Microsoft.App/containerApps/stop/action`、`Microsoft.App/containerApps/start/action`（Contributor・Container Apps Contributor に含まれる）
- 停止した Container App の更新でリビジョンがどう扱われるか（開始で新しいリビジョンが動くか）は、Azure で確認する。うまくいかない場合は、レプリカ数を 0 にする更新 → 本来の更新、の 2 段階に変える
- 基盤のメンテナンスでの再起動は本ツールでは制御できない。README に「まれにデータが壊れるおそれがある。検証環境向けで、重要なデータは Timetracker の機能でエクスポートしておく」と書く

## 5. 引数
### 5.1 CLI
```
timetracker-cli deploy ... [--persist-db [true|false]]
timetracker-cli delete ... [--keep-db-data [true|false]]
```
- `--persist-db`: DB のデータを Azure Files に置く。既存の bool フラグと同じ書式（`--persist-db`、`--persist-db true`、`--persist-db=false`）
- 【決定 Q3】指定しない場合は、既存の DB アプリの今の状態を引き継ぐ（永続化していれば続け、していなければ永続化しない。新規は永続化しない）。永続化をやめるには `--persist-db false` を明示する。このため `DeployOptions.PersistDb` は `bool?`（null は「今の状態を引き継ぐ」）とし、計画（`DeploymentPlanner`）と実行（`DeploymentRunner`）が、既存の DB アプリのボリュームを読んで実際に永続化するかを決める
- `--keep-db-data`（delete）: 永続化した DB のデータ（ストレージアカウント）を残す（8）。`--dry-run` の deploy で `--persist-db` を指定した場合は警告を出して無視する

### 5.2 MCP
- `validate_deployment` / `plan_deployment` / `generate_compose` の共通パラメータ（全体設計 4.2）に `persist_db`（bool?、既定 null）を加える。`DeploymentParameters` → `DeployInput.PersistDb` → `DeployOptions.PersistDb`
- `get_deployment_options` に `persist_db` の説明（既定、料金の目安、制約）を加える
- `plan_deletion` に `keep_db_data`（bool、既定 false）
- `plan_deployment` の結果に `persist_db`（実際に永続化するか）と `storage_account`（永続化する場合のストレージアカウント名）、`get_deployment_info` の結果に `persistence`（DB アプリが Azure Files を使っているか、ストレージアカウント）を加える
- `start_*` は従来どおり計画の内容で実行し、新しい引数は加えない

## 6. 計画と再デプロイの警告（`acknowledge_data_loss`）
`DeploymentPlanner` は、既存の DB アプリのボリュームを読み、今の永続化の状態を判定する（`ContainerAppInfo` にボリュームの一覧を加える）。

| 既存の DB アプリ | 今回の指定 | DB のデータ | `requires_data_loss_ack` | 警告・表示 |
|---|---|---|---|---|
| 無い | どちらでも | 新規 | false | 永続化する場合: ストレージを作ること、料金の案内 |
| 永続化なし | 永続化なし | 設定が変わると失われる | **true**（今と同じ） | 今と同じ |
| 永続化なし | 永続化する | **今のデータは失われる**（共有にあるデータから始まる。新規の共有なら空） | **true** | 「永続化に切り替えると、今の DB のデータは引き継がれません」 |
| 永続化あり | 永続化する | 残る | **false** | 「DB アプリを停止してから更新します（その間は接続できません）」 |
| 永続化あり | 指定なし | 残る（Q3 で今の状態を引き継ぐ） | **false** | 永続化ありの行と同じ |
| 永続化あり | 永続化なし（`--persist-db false`） | 共有には残るが、DB は空から始まる | **true** | 「永続化をやめると、DB は空から始まります（ストレージアカウントのデータは残ります）」 |

- 計画の操作（`PlannedOperation`）に、ストレージアカウント・ファイル共有・Environment のストレージを加える（`PlannedResourceKind` に `StorageAccount` / `FileShare` / `EnvironmentStorage`）。`HasSameOperations` の比較の対象にも含まれる
- `PlannedOperation.DataLoss` の判定を上の表に変える。`DeploymentRunner` は、永続化した既存の DB アプリの更新で、4.1 の停止 → 更新 → 開始を行う
- CLI のデプロイ時の警告（`WarnDbDataLoss`）も同じ表で判定する（永続化を続ける場合は警告しない）
- ストレージアカウントの名前が別のリソースグループ・サブスクリプションで使われている（`checkNameAvailability` で使えない）場合はブロッカーにする（ハッシュで避けているが、念のため）

## 7. 権限の事前確認
`AzurePermissions` に定数を加え、`DeploymentPlanner.CheckPermissionsAsync` の要件に、永続化する場合だけ加える。既存のリソースへの操作は、従来どおりリソースの権限でも判定する（`TargetResource` に `Microsoft.Storage` の種類を加え、`GetResourcePermissionsAsync` のプロバイダーを引数にする）。

| 処理 | 必要な権限 |
|---|---|
| デプロイ（永続化する） | `Microsoft.Storage/storageAccounts/read`、`.../write`（作成する場合）、`.../listkeys/action`、`Microsoft.Storage/storageAccounts/fileServices/shares/read`、`.../write`（作成する場合）、`Microsoft.App/managedEnvironments/storages/read`、`.../write` |
| デプロイ（永続化した既存の DB アプリの更新） | 上記 ＋ `Microsoft.App/containerApps/stop/action`、`Microsoft.App/containerApps/start/action` |
| 削除（データも消す場合） | `Microsoft.Storage/storageAccounts/delete` |

- Contributor（サブスクリプション・リソースグループ）はすべて含む。README の推奨ロールは変わらない
- リソースプロバイダー `Microsoft.Storage` の登録が必要（README の手順に加える）
- 反映されないもの（実行時のエラーになる）: Azure Policy で、アカウントキーによるアクセス（`allowSharedKeyAccess`）やパブリック ネットワーク アクセスを禁止している場合。SMB のマウントにはどちらも要るため、README と計画の注意に書く

## 8. 削除での扱い【決定 Q2: 既定で消す】
削除の規則（管理タグで判定、`include_untagged`、別の所有者のタグは対象外）は、ストレージアカウントにも同じものを当てはめる。Environment を削除すると、その子の Environment のストレージ（キーの登録）も消える。

- 既定: ストレージアカウントも削除の対象にする（今の「DB アプリを削除すると DB のデータはすべて失われます」と同じ考え方で、`delete` で料金のかかるものが残らない）。計画の警告に「ストレージアカウント（Azure Files に永続化した DB のデータ）も削除します。元に戻せません（残すには --keep-db-data）」を出す
- `--keep-db-data` / `keep_db_data=true`: ストレージアカウントを残す（対象の理由は「--keep-db-data の指定により残します（DB のデータ）」）。計画と完了時に「料金がかかり続けること、同じアプリ名で `--persist-db` を付けてデプロイするとデータを引き継ぐこと、不要になったら `--keep-db-data` を付けずに delete すること」を示す
- ストレージアカウントは、永続化していない環境でも削除の対象の一覧に載せる（「存在しません」）。削除の直前の確かめ直しで、名前で対象を探すため
- データを消す場合の確認は今のアプリ名の入力（CLI）・`confirm_app_name`（MCP）で行い、新しい確認は加えない
- 「DB のデータはすべて失われます」の警告は、DB アプリを削除し、かつストレージアカウントを残さない場合に出す
- 削除の順: `-tt` → `-redis` → `-db` → `-env` → ストレージアカウント

## 9. 料金の案内
- 永続化した場合に増えるのは、Azure Files（Standard、LRS、従量課金）の**保存容量**と**トランザクション**の料金。DB のファイルは小さい（サンプル DB で数百 MB 程度の見込み）が、DB は読み書きが多いためトランザクションの料金が主になる見込み。具体的な金額は [Azure Files の料金](https://azure.microsoft.com/pricing/details/storage/files/) で確認するよう案内し、ツールに金額は書かない（料金は変わるため）
- Azure での確認（10）で、1 日動かしたときの実際の料金を Cost Management で確かめ、README に「目安」として書くかを決める
- `--keep-db-data` で削除した場合は、ストレージアカウントの料金がかかり続けることを示す

## 10. 確認（Azure）
実際の Azure へのデプロイは、行う前に利用者の了承を得る。

1. PostgreSQL ＋ `--persist-db`: デプロイ → サンプル DB の作成 → ログイン → 設定を変えて再デプロイ（停止 → 更新 → 開始）→ データが残ること → DB アプリの再起動（`az containerapp revision restart`）→ データが残ること
2. SQL Server ＋ `--persist-db`: 同上（サンプル DB は Q4 の結果による）
3. 永続化なし → 永続化あり、永続化あり → なしの切り替えで、計画の `requires_data_loss_ack` と警告が 6 の表どおりになること
4. 削除: 既定でストレージアカウントも消えること。`--keep-db-data` で残した場合に、同じアプリ名でデプロイし直してデータが戻ること
5. 権限: Reader ＋ Container Apps Contributor の ID で、Storage の権限の不足が事前確認で止まること（システムテストの `LimitedPermissionTests` に加える）
6. 確かめて決めること: PostgreSQL が SMB で動くか、`mountOptions`（`nobrl` などの要否、SQL Server の gid）、停止した Container App の更新の動き（4.1）、1 日の料金

## 11. 実装の分担
| 層 | 追加・変更 |
|---|---|
| Domain | `DeployOptions` / `DeployInput.PersistDb`、`DeployOptionsResolver`、`DeploymentValidator`（PostgreSQL で動かない場合のエラー、Dry-run の警告）、`ResourceNaming`（ストレージアカウント名・共有名・Environment のストレージ名）、`AzurePermissions`（7 の定数）、`ContainerImages`（マウント先・uid） |
| Infrastructure.Azure | `Azure.ResourceManager.Storage` を追加。`IAzureDeploymentGateway` にストレージアカウント・共有・Environment のストレージの確認と作成、DB アプリの停止と開始、ストレージアカウントの削除、名前の確認。`ContainerAppInfo` にボリューム。`ContainerAppSpecBuilder.BuildDbApp` にボリュームとマウント、`BuildEnvironmentStorage` |
| Application | `DeploymentPlanner`（操作・`DataLoss` の判定・権限）、`DeploymentRunner`（ストレージの手順、停止 → 更新 → 開始）、`DeletionPlanner` / `DeletionRunner`（ストレージアカウント）、`DeploymentInspector`（永続化の状態） |
| CLI | `--persist-db`（deploy）、`--keep-db-data`（delete）、ヘルプ、計画・警告の表示 |
| MCP | 共通パラメータ `persist_db`、`plan_deletion` の `keep_db_data`、結果の `persist_db` / `persistence`、`get_deployment_options`、instructions・Prompts・Resources（constraints / rbac） |
| 文書 | README（目的と注意事項・作成されるリソース・RBAC・リソースプロバイダー・削除・パラメータ・FAQ）、`docs/MCP_TOOL_DESIGN.md`（4.2 / 4.12 / 4.13）、`docs/ROADMAP.md`、`docs/REVIEW_ISSUES.md`（No.46） |

## 12. テスト
- C1（分岐網羅）100% を維持する（`Program.cs` の終了処理と正規表現の生成コードを除く）。修正前に失敗し、修正後に成功することを確かめる
- Domain: 引数の解決と検証、ストレージアカウント名（長さ・文字種・同じ入力で同じ名前・アプリ名の長さの境界）
- Infrastructure: `BuildDbApp` のボリューム・マウント・`PGDATA`・`mountOptions`（DB 種別ごと、永続化しない場合は今と同じであること）、Environment のストレージの定義（キーを含む）、ストレージの作成・既存の再利用・停止と開始の呼び出し（`ArmClient` のモック）
- Application: 6 の表のすべての行、権限の要件（作成・既存・停止）、削除の対象と順序（既定と `--keep-db-data`）、直前の確かめ直し
- CLI / MCP: 引数の組み合わせ（delete で `--persist-db` はエラーなど）、計画の表示と結果、警告、`acknowledge_data_loss` が不要になる場合・必要な場合
- Azure（システムテスト）: 10 の 1・4・5 を `DeploymentLifecycleTests` / `LimitedPermissionTests` に加える（費用がかかるため、永続化のシナリオは 1 回にまとめる）

## 13. 利用者の判断（決定）
| No. | 質問 | 決定 | 補足 |
|---|---|---|---|
| Q1 | プロトコル | SMB | 推奨どおり（3） |
| Q2 | 削除のときのデータ | 既定で消す（`--keep-db-data` / `keep_db_data` で残す） | 推奨（残す）とは逆。`delete` で料金のかかるものを残さない（8） |
| Q3 | 永続化した環境に `--persist-db` なしで再デプロイしたとき | 今の状態を引き継ぐ（やめるには `--persist-db false`、データ消失の同意が要る） | 推奨どおり（5.1・6） |
| Q4 | Azure 上の SQL Server のサンプル DB（No.19） | 別の項目にする | 推奨どおり（1） |

## 14. 実施状況
- 実装: 11 の分担のとおり（REVIEW_ISSUES No.46）。自動テストは全件成功、分岐網羅率は正規表現の生成コードを除き 100%
- 未実施: 10 の Azure での確認と、システムテスト（`DeploymentLifecycleTests` / `LimitedPermissionTests`）への永続化のシナリオの追加。Azure へのデプロイは料金がかかるため、オーナーの了承を得てから行う。確認の結果で、`mountOptions`（`nobrl` などの要否、SQL Server の gid）、停止 → 更新 → 開始の手順、PostgreSQL での可否（動かない場合は PostgreSQL では `--persist-db` をエラーにする）を見直す
