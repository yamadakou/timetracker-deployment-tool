# 詳細設計: 削除のときに、リソースグループも削除する

- 対象ブランチ: `feature/desktop-app`
- 状況: 論点はオーナーの回答で決めた（7）。一次情報の確認日は 2026-10-20
- 記録: `docs/REVIEW_ISSUES.md` の No.53

## 1. 目的
今の削除（CLI の `delete`、MCP の `plan_deletion` / `start_deletion`、デスクトップアプリの「削除」）は、本ツールで作ったリソース（管理タグの付いた Container App 3 つ・Container Apps Environment・DB のデータのストレージアカウント）だけを消し、リソースグループは残す。本ツールは既存のリソースグループにもデプロイでき、リソースグループが本ツール専用とは限らないためである。

そのため、本ツールのためだけに作ったリソースグループ（例: オーナーの実機の確認に使った `tt-deploy-test`）は、削除の後に空のまま残り、ポータルや `az group delete` で消す必要があった。オーナーの依頼（2026-10-20）で、削除のときに、利用者が選べばリソースグループも削除できるようにする。CLI にはオプションを足す。

## 2. 一次情報（Microsoft Learn）で確かめたこと
| 項目 | 内容 | 設計への影響 |
|---|---|---|
| 削除の順序 | リソースグループを削除すると、Resource Manager が中のリソースの削除の順序を決め、子のリソース → 他を管理するリソース（`managedBy`）→ 残り、の順に消す | 中に残っているリソースは、本ツールのものかどうかに関係なく、すべて消える。誤って巻き込まないよう、中身を確かめる（3） |
| 元に戻せない | 「Resource group deletion is irreversible.」。削除したリソースグループは復元できない | 計画の表示と警告で、元に戻せないことを示す（4.1。確認の入力はアプリ名だけ。Q2） |
| 権限 | リソースグループの削除に要るのは `Microsoft.Resources/subscriptions/resourceGroups/delete` だけで、中のリソースを個別に削除する権限は要らない | 計画で、この権限を事前に確かめる。README の権限の表に加える |
| ロック | 削除の前にロック（`CanNotDelete` など）を外す必要がある。手で付けていなくても、関連するサービスが自動でロックを付けることがある。ほかのリソースグループのリソースとつながっていると、削除に失敗することがある | 削除に失敗したら、リソースグループは残ったと伝え、理由（Azure のエラー）を示す（4.3） |
| SDK | `ResourceGroupResource.GetGenericResourcesAsync()` で、リソースグループの中のリソースを一覧できる。`ResourceGroupResource.DeleteAsync(WaitUntil, forceDeletionTypes)` で削除する（`forceDeletionTypes` は仮想マシンと VM スケール セットだけが対象のため、使わない） | Azure の窓口（`IAzureDeploymentGateway`）に、一覧と削除の 2 つを足す |

Container Apps の環境は、本ツールのように既定のネットワーク（独自の仮想ネットワークを使わない）で作る場合、同じリソースグループに環境とアプリ以外のリソースを作るという記載は見つからなかった。念のため、半自動の UI テストと System Test で、削除の後にリソースグループが空になることを確かめる（6）。

## 3. 削除してよい条件
リソースグループは、次をすべて満たす場合だけ削除する。
1. 利用者が「リソースグループも削除する」を選んだ（既定では削除しない。今と同じ）
2. 本ツールのリソースを削除した後に、リソースグループの中にリソースが 1 つも残らない
   - 計画の時点で、リソースグループの中のリソースを一覧し、今回の削除の対象（`WillDelete` のもの）以外のリソースが 1 つでもあれば、リソースグループは削除しないと計画に出す（残るリソースの名前と種類を、最大 10 件示す）
   - 同じリソースグループに、別のアプリ名でデプロイした環境があれば、それも「残るリソース」になる（ほかのアプリの環境を巻き込まない）
   - 「DB のデータを残す」（`--keep-db-data`）で残すストレージアカウントも「残るリソース」になる（特別な規則にせず、残るリソースとして同じく示す）
   - 管理タグの無いリソースを対象にする（`--include-untagged`）場合は、対象にしたものも消すリソースとして数える
3. 権限（`Microsoft.Resources/subscriptions/resourceGroups/delete`）がある（足りなければ、ほかの権限の不足と同じくブロッカーにする）

利用者がリソースグループの削除を選んだのに、2 を満たさない（ほかのリソースが残る）場合は、ブロッカーにして何も削除しない（選んだ利用者が、リソースグループが残ることに気付かないまま本ツールのリソースだけを消さないよう）。リソースグループを残してよければ、選ぶのをやめて計画し直す。リソースグループが無い場合はブロッカーにしない。本ツールのリソースが既に無く、空のリソースグループだけが残っている場合（例: 以前に本ツールのリソースだけを削除した `tt-deploy-test`）も、リソースグループの削除だけを行える。

実行の手順:
1. 今と同じ順で、本ツールのリソースを削除する
2. リソースグループの中を一覧し直し、空であることを確かめる。削除の直後は一覧に反映されないことがあるため、空になるまで一定の間（2 分）待って読み直す
3. 空ならリソースグループを削除する。空にならなければ、リソースグループは削除せずに、残っているリソースを示して失敗にする（本ツールのリソースの削除は終わっている）

「中のリソースがすべて本ツールのものなら、リソースグループごと一度に消す」（手順 1 を省く）案は、Q1 で採らないことにした。

## 4. 各フロントエンド
### 4.1 計画の表示
- 「リソースグループ: 削除する」「リソースグループ: 削除しない（理由）」の行を、今の「リソースグループ … は削除しません」の行の代わりに出す。理由は「選んでいません」「ほかのリソースが残ります: 名前（種類）…」「DB のデータを残すため」「権限が不足しています」など
- 削除する場合は「リソースグループ {名前} と、その中のものは元に戻せません」と警告する

### 4.2 指定と確認
| フロントエンド | 指定 | 確認 |
|---|---|---|
| CLI | `delete --delete-resource-group [true\|false]`（既定 false） | 今と同じく、アプリ名の入力だけ（Q2）。`--yes` で省く |
| デスクトップアプリ | 「削除」の画面に、チェックボックス「リソースグループも削除する（本ツールのリソースを削除した後に、中が空になる場合だけ。元に戻せません）」 | 今と同じく、アプリ名の入力だけ（Q2） |
| MCP サーバー | `plan_deletion` の `delete_resource_group`（既定 false）（Q3） | 今と同じく、`start_deletion` の `confirm_app_name` だけ（Q2） |

### 4.3 結果
- 成功: 「削除が完了しました（リソースグループ {名前} も削除しました）。」。結果（`DeletionOutcome`）に、リソースグループを削除したかを加える
- リソースグループの削除に失敗（ロックなど）: 本ツールのリソースは削除済みで、リソースグループは残った旨と、Azure のエラーを示し、ジョブは失敗にする（CLI は終了コード 1）。もう一度実行すると、本ツールのリソースは「既に無い」となり、リソースグループの削除だけを行う

## 5. 変える箇所
| 層 | 変更 |
|---|---|
| Infrastructure | `IAzureDeploymentGateway` に `ListResourcesAsync`（リソースグループの中のリソースの名前・種類）と `DeleteResourceGroupAsync`（無ければ false）を足す。`AzureSdkExecutor` で SDK を呼ぶ |
| Domain | `AzurePermissions.ResourceGroupDelete` |
| Application | `DeletionPlanner.PlanAsync` に `deleteResourceGroup` を足し（省略時 false。今と同じ）、`DeletionPlan` にリソースグループの判定（削除するか・理由・残るリソース）を持たせる。`DeletionRunner` が最後にリソースグループを削除する。実行の直前の確認（`HasSameDeletions`）でも、リソースグループの判定を比べる |
| CLI | `--delete-resource-group`、計画の表示、確認の入力、ヘルプ、README |
| MCP | `plan_deletion` の引数と結果、`start_deletion` の確認、instructions・Resources の説明（Q3） |
| デスクトップアプリ | `DeletionViewModel` の選択・確認の入力・表示、`MainWindow.xaml` |

既定（指定しない場合）の動きは、すべてのフロントエンドで今と変えない。

## 6. テスト
- Application: 計画の判定（選ばない・空になる・ほかのリソースが残る・別のアプリの環境が残る・`keep-db-data` と同時・権限が無い・リソースグループが無い）、実行（空になってから削除・一覧の反映の遅れ・空にならない・削除の失敗・既に無い）。分岐網羅率 100%
- Infrastructure: 一覧と削除の SDK の呼び出し（今の `AzureSdkExecutor` のテストと同じ方法）
- CLI・MCP・Desktop.Core: 指定・確認の入力の一致と不一致・表示。分岐網羅率 100%
- System Test（CLI）と Desktop System Test: 新しいリソースグループにデプロイし、リソースグループごと削除して、リソースグループが無くなることを確かめる（Azure での実行の前にオーナーの確認を取る）

## 7. 論点と決定（オーナーの回答、2026-10-20）
| # | 決定 |
|---|---|
| Q1 | A（推奨どおり）: 本ツールのリソースを消し、空になったのを確かめてからリソースグループを消す |
| Q2 | B: 確認の入力はアプリ名だけ（今と同じ）。計画の表示と警告（「元に戻せません」）で知らせる |
| Q3 | A（推奨どおり）: MCP サーバーにも加える |
| Q4 | B: 期限での自動の削除（7d）では、デプロイのときに本ツールが作ったリソースグループだけを、空になれば削除する。作ったことを示すため、今回から、デプロイで新しく作るリソースグループに `managed-by=ttdeploytool` のタグを付ける（`tt-app-name` は付けない。複数のアプリ名で共有できるため）。既存のリソースグループは変えない。自動の削除での使い方は 7d で実装する |

検討した選択肢:
| # | 論点 | 選択肢 | 推奨 |
|---|---|---|---|
| Q1 | 削除の仕方 | A: 本ツールのリソースを今と同じ順に消し、リソースグループが空になったのを確かめてから消す（3）。B: 中のリソースがすべて本ツールのものなら、リソースグループの削除だけを行う（Azure が中をまとめて消す） | A。削除の直前まで中身を確かめ直せ、計画の後にほかのリソースが加わっても巻き込まない。B は速いが、一覧と削除の間に加わったリソースも消える |
| Q2 | 確認の入力 | A: アプリ名に加えて、リソースグループ名も入力させる。B: アプリ名だけ（今と同じ） | A。リソースグループの削除は元に戻せず、Azure ポータルもリソースグループ名の入力を求めるため |
| Q3 | MCP サーバーにも加えるか | A: 加える（同じ条件・確認）。B: 加えない（CLI とデスクトップアプリだけ） | A。条件（空になる場合だけ）と確認が同じなら、AI エージェントから使っても巻き込みの危険は増えない。MCP の操作してよい範囲（リソースグループの許可の一覧）も今と同じく効く |
| Q4 | 期限での自動の削除（7d）でも、リソースグループを削除するか | A: 削除しない（本ツールのリソースだけ）。B: デプロイのときにリソースグループを作った場合だけ、空になれば削除する（作ったことをタグで記録する） | A。自動の削除は利用者の確認が無いため、範囲を広げない。必要になれば 7d で見直す |

## 8. 出典（2026-10-20 に確認）
- リソースグループとリソースの削除（順序・権限・ロック・元に戻せない）: https://learn.microsoft.com/azure/azure-resource-manager/management/delete-resource-group
- `ResourceGroupResource.GetGenericResourcesAsync`: https://learn.microsoft.com/dotnet/api/azure.resourcemanager.resources.resourcegroupresource.getgenericresourcesasync
- `ResourceGroupResource.DeleteAsync`: https://learn.microsoft.com/dotnet/api/azure.resourcemanager.resources.resourcegroupresource.deleteasync
- Resource Groups - Delete（REST）: https://learn.microsoft.com/rest/api/resources/resource-groups/delete
- Container Apps のネットワーク（既定のネットワークと独自の仮想ネットワーク）: https://learn.microsoft.com/azure/container-apps/networking

## 9. 実施結果
- Infrastructure: `ListResourcesAsync`（`GetGenericResourcesAsync`）、`DeleteResourceGroupAsync`（`ResourceGroupResource.DeleteAsync`）。リソースグループを新しく作るときに `managed-by` のタグを付け、`GetResourceGroupAsync` がタグを返す（`ResourceGroupInfo.CreatedByTool`）
- Application: `DeletionPlanner.PlanAsync(..., deleteResourceGroup)` と `PlanResourceGroupAsync`（名前と種類で、削除するもの以外が残るかを比べる。大文字と小文字は区別しない）、`DeletionPlan.ResourceGroupTarget` / `DeletesResourceGroup` / `ResourceGroupBlocker`、`DeletionRunner.DeleteResourceGroupAsync`（空になるまで 10 秒ごとに最大 2 分読み直す。ロックなどで失敗したら理由と次の手順を示す）、`DeletionOutcome.ResourceGroupDeleted`
- CLI: `--delete-resource-group`、計画・警告・完了の表示、ヘルプ、README
- MCP: `plan_deletion` の `delete_resource_group`、結果の `targets`（`resource_group`）・`deletes_resource_group`・`kept`、ジョブの結果の `resource_group_deleted`（`kept_resource_group` は削除した場合 null）、instructions・Resources・Prompts
- デスクトップアプリ: 「削除」の画面のチェックボックス、計画の一覧のリソースグループの行、警告、結果
- テスト: 単体テストを追加（Domain・Infrastructure・Application・CLI・MCP・Desktop.Core）。ソリューション全体 925 件（スキップ 5）成功。追加・変更したコードの行と分岐はすべて網羅（残る未網羅は、既存の Program.cs と、既存の 2 か所の分岐）。半自動の UI テスト（`AzureUiTests`）の最後に、空になったリソースグループを「リソースグループも削除する」で消す確認を加えた
- 本物の Azure での確認: オーナーの確認の後、Desktop System Test（run 39429755120、UI テスト約 17 分）で成功。本ツールのリソースを削除した後の空のリソースグループ（`rg-ttui-39429755120`）を「リソースグループも削除する」で計画すると、警告「…も削除します。元に戻せません。」が出て、実行するとリソースグループの削除だけを行い（約 18 秒）、結果に「リソースグループ … も削除しました。」が出た。ワークフローの後片付けの時点で、リソースグループは既に無かった（後片付けは削除をしなかった）

