# 詳細設計: デスクトップアプリの UI テスト（Phase 6）

- 対象ブランチ: `feature/desktop-app`
- 前の段階: `docs/design/desktop/desktop-app.md`（Phase 5。13 のテストの方針では、WinUI 3 の画面を自動テストの対象にしていなかった）
- 状況: 案。オーナーの回答で決めたこと: 道具は FlaUI、範囲は CI とローカルの半自動の両方を段階的に、診断用のログ（`desktop-app.md` の 7.2）を先に入れる。8 の論点もオーナーの回答で決めた（CI は署名した MSIX・Dry-run は保存まで・半自動は実行と削除まで）。一次情報の確認日は 2026-10-19
- 記録: `docs/REVIEW_ISSUES.md` の No.51（予定）

## 1. 目的
Windows の実機での確認で、次の問題が見つかった。どれも `Desktop.Core` の単体テスト（Linux で動く ViewModel のテスト）では見つけられない。
- Visual Studio でビルドすると、デスクトップアプリのプロジェクトが省かれて起動できない（`.slnx` のプラットフォームの対応付け）
- 起動プロファイルが無く、MSIX として起動できない
- サインインの後にサブスクリプションを読めないなど、本物のサインイン（WAM）と Azure での動きが不安定

UI テストでは、実際に起動したアプリを Windows の UI Automation で操作し、次を確かめる。
- 画面と ViewModel の結び付き（`x:Bind`、選択の変更イベント、ボタンの有効・無効、表示の切り替え）が正しいこと（CI。PR ごと）
- 本物のサインインと Azure で、サブスクリプションの読み込みから計画・実行・削除までが通ること（ローカルの半自動。サインインだけ人が行う）

## 2. スコープ
| 項目 | Phase 6 |
|---|---|
| 画面の要素に AutomationId を付ける | ✔（6a） |
| CI で、Azure を使わない操作の UI テスト | ✔（6a） |
| ローカルの半自動の UI テスト（本物のサインインと Azure） | ✔（6b） |
| 偽のサインインと Azure を差し込むテスト用のビルド | ✘（範囲の回答で選ばなかった） |
| 本物の Azure を Azure CLI のサインインで使うテスト用のビルドと、UI テストの System Test | ✔（6c。オーナー判断で追加。実機の確認の P1〜P4 を、人が操作せずに確かめるため） |
| UI テストの網羅率の測定 | ✘（`Desktop` のプロジェクトは網羅率の対象外のまま。ViewModel の網羅率 100% は `Desktop.Core` のテストで保つ） |
| 画面の見た目（スクリーンショットの比較） | ✘。失敗したときのスクリーンショットだけを成果物に残す |

## 3. 道具（決定: FlaUI）
| 候補 | 内容 | 判断 |
|---|---|---|
| **FlaUI**（FlaUI.UIA3 5.0.0、2025-02 公開、OSS） | .NET から UI Automation（UIA3）を使うライブラリ。xUnit v3 のテストとして書け、System Test と同じ実行方法・結果の形式（trx）にできる | **採用**（オーナー判断） |
| winapp CLI（`winapp ui`） | Microsoft の公式のコマンド。`inspect` / `invoke` / `set-value` / `wait-for` / `screenshot` と、アプリの登録・起動（`winapp run`）。UI Automation のパターンで操作する命令は、ロックされた画面や CI でも動くと記載されている | 不採用。パブリック プレビューで、テストは PowerShell からコマンドを呼ぶ形になる。調査（`winapp ui inspect`）には使える |
| WinAppDriver / Appium の Windows ドライバー | WebDriver の形式 | 不採用。WinAppDriver は 2022 年以降更新されておらず、Appium の Windows ドライバーもその上に作られている |

操作は UI Automation のパターン（Invoke・Value・SelectionItem・Toggle）で行い、マウスやキーボードの入力の合成は使わない。入力の合成は、前面のウィンドウと操作できるデスクトップが要り、CI で不安定になるため（winapp CLI の説明でも、CI ではパターンを使う命令を勧めている）。

## 4. 構成
### 4.1 プロジェクト
- `tests/TTDeployTool.Controller.Desktop.UITests`（新規。`net10.0-windows10.0.19041.0`、xUnit v3、FluentAssertions、FlaUI.UIA3）。Windows でしか動かないため `TTDeployTool.sln` には入れず、`TTDeployTool.Desktop.slnx` に入れる
- テストの種類はクラスで分ける: `CiUiTests`（Azure を使わない。常に実行）と `AzureUiTests`（本物の Azure。設定が無ければスキップする。System Test の `TT_SYSTEMTEST_SUBSCRIPTION_ID` と同じ考え方）
- 共通の部品: アプリの起動と終了、画面の切り替え（`NavigationView` の項目の選択）、要素の待ち合わせ（`Retry.WhileNull` などで最大 N 秒）、失敗したときのスクリーンショットの保存

### 4.2 AutomationId
`MainWindow.xaml` の、テストで使う要素に `AutomationProperties.AutomationId` を付ける（Microsoft Learn: 言語によらず同じ値にし、テストの自動化に使う）。名前は画面と要素で決める（例: `Nav.Target`、`SignIn.Button`、`SignIn.Account`、`Target.Subscription`、`Target.Reload`、`Target.ResourceGroup`、`Target.Error`、`DryRun.Save`、`Jobs.LogFolder`）。表示の文言は変わることがあるため、テストでは文言ではなく AutomationId で要素を探す（文言は、表示の内容を確かめるときだけ使う）。

### 4.3 起動する対象
| 実行の場所 | 起動のしかた |
|---|---|
| CI | x64 の MSIX を、ジョブの中で作った一時的な自己署名の証明書（発行者は `CN=TTDeployTool`）で署名し、公開鍵の証明書を「信頼されたユーザー」に入れて導入し、AppUserModelID で起動する（Q1） |
| ローカル | Visual Studio の配置（開発者モード）または導入したパッケージを、AppUserModelID（`<パッケージ ファミリ名>!App`）で起動する（`Application.LaunchStoreApp`） |

テストの前後で、アプリのデータ（サインインの情報・診断用のログ）を消すかどうかは、CI では毎回新しい環境のため気にしない。ローカルでは、利用者のサインインの情報を残す（半自動のテストでサインインを何度も求めないため）。

## 5. CI のテスト（6a）
Azure を使わず、サインインもしない。
| テスト | 確かめること |
|---|---|
| 起動 | ウィンドウが開き、タイトルが「TTDeployTool」。処理されなかった例外が無い（診断用のログに `Critical` が無い） |
| 画面の切り替え | `NavigationView` の 7 つの項目を選ぶと、対応する画面だけが表示される |
| サインインしていない状態 | 「サインインしていません」の表示。サインインのボタンが押せ、サインアウトのボタンは押せない |
| 対象の入力の確かめ | 何も入れないと「サブスクリプションを選んでください。」。アプリ名に誤った値を入れると誤りの表示が変わる（`TargetError` の結び付き） |
| 設定の結び付き | DB の種別・永続化の ComboBox を選ぶと、ViewModel に戻る（選択の変更イベント。5b で `BindBack` から直した箇所） |
| Dry-run | 誤った設定（パスワードなし）ではエラーを表示して保存しない。正しい設定（SQL Server・永続化を選ぶ）では、OS のフォルダーの選択のダイアログで一時フォルダーを選び（共通のダイアログの ID: 入力欄 1152・選択 1）、`docker-compose.yml` と `.env` ができ、SQL Server のイメージの内容・永続化を使わない旨の警告・次の手順が表示される（Q2） |
| 診断用のログ | 「ジョブ」にログのフォルダーが表示され、`ttdeploytool-desktop-yyyyMMdd.log` に「起動しました」がある |
| 閉じる | 実行中のジョブが無ければ、確認なしで閉じる |
| ジョブの画面（7b） | ジョブが無ければ、状況（`Jobs.Summary`）が空で、上部の帯（`Jobs.Banner`）を出さない |
| 1 つだけ起動する（7c） | 起動中にもう一度起動すると、2 つ目は起動を渡して終わり、アプリのプロセスは 1 つのまま |
| 同時に実行する数（7b） | 「設定」の選択肢が 1〜4 で既定が 2。3 を選んでアプリを起動し直すと 3 のまま（アプリのデータの `preferences.json`）。最後に 2 に戻す |

CI のジョブ: `ci.yml` に `desktop-ui`（windows-latest）を加える。`desktop` ジョブと同じ条件（コードの変更があるとき）で動かし、テストの結果（trx）と失敗したときのスクリーンショットを成果物に残す。GitHub のホストされた Windows ランナーで UI Automation が動くか（操作できるデスクトップのセッションがあるか）は公式の記載を見つけられなかったため、6a の最初に、起動とタイトルの確認だけのテストで確かめる（動かない場合は、Q1 と合わせて方式を見直す）。

## 6. ローカルの半自動のテスト（6b）
本物のサインイン（WAM）と Azure を使う。サインインだけ人が行う（`scripts/run-system-tests.ps1` と同じ考え方）。
| 段階 | 内容 |
|---|---|
| 準備 | `scripts/run-desktop-ui-tests.ps1` が、対象のサブスクリプションとリソースグループ（既定は `rg-ttui-<日時>`）を受け取り、実行の前に、作るもの・料金・後片付けを示して確認を求める |
| サインイン | サインインしていなければ、テストが「サインイン」を押し、利用者が WAM の画面でサインインするのを最大 5 分待つ |
| サブスクリプションの読み込み | 一覧に対象のサブスクリプションがあること、エラーの表示が無いこと。読めなかったテナントの注意は、内容を結果に書き出す（失敗にはしない）。「読み直す」を続けて押しても、一覧が重複しないこと |
| 状態の確認 | まだ何も無いリソースグループで「見つからない」と表示されること |
| デプロイと削除 | 計画 → 実行で、Timetracker の URL が表示されるまで。その後、状態の確認と、削除（アプリ名の入力）まで行う（Q3） |
| 結果 | テストの結果（trx）、スクリーンショット、診断用のログの写しを、`TestResults` に置く。ログはオーナーが確かめてから、REVIEW_ISSUES に要約を書く |

## 6.1 UI テストの System Test（6c）
実機の確認で見つかった問題（`desktop-jobs.md` の P1〜P4）を、オーナーの PC での操作なしに、本物の Azure で確かめるため、6 のテスト（`AzureUiTests`）を GitHub Actions で実行する（オーナー判断。Azure に Windows の VM を作る案と比べ、VM の料金と自動ログオンの設定が要らないため）。
- サインイン: WAM の対話サインインは人が要るため、テスト用のビルド（`/p:UiTestSignIn=true`。条件付きコンパイルの `UITEST_SIGNIN`）でだけ、`AzureCliSignInService`（Azure.Identity の `AzureCliCredential`）を使う。ワークフローの `azure/login`（OIDC。System Test と同じアプリ登録・環境 `systemtest`）でサインインした Azure CLI を、アプリがそのまま使う。秘密の情報をアプリに渡さず、保存もしない。CI の `desktop` ジョブとリリースはこの指定をしないため、Store に出すパッケージには入らない（「製品のコードにテスト用の入口を作らない」方針を、この範囲で変えた）
- ワークフロー: `.github/workflows/desktop-system-test.yml`（手動実行・環境 `systemtest` の承認）。windows-latest で、MSIX を一時的な自己署名の証明書で署名して導入し、`AzureUiTests` を実行する。リソースグループは `rg-ttui-<実行の ID>` で、最後に（失敗しても）削除する。結果（trx・失敗したときのスクリーンショット・アプリの診断用のログ）を成果物に残す
- 確かめること（6 の流れに加えて）: P1（パスワードの無い計画でエラーが出て、直して計画し直すとエラーの領域ごと消える）、P3（計画の間、処理中の表示と Azure を読む段階が出る）、P2（デプロイの実行中に状態を確認すると「実行中のため」の注意が出て、環境が無ければ永続化の行は出ない）、P4（同じ対象を削除している間、デプロイの画面に削除のジョブも結果も出ず、「Timetracker を開く」が押せない）
- 実施結果（2026-10-20）: ワークフローのファイルを PR #47 で `main` に入れ、`feature/desktop-app` を選んで実行した。1 回目（run 39400218403）は、計画の一覧（ItemsControl）を AutomationId で探して止まった（アプリは正しく表示していた。Azure にはリソースを作る前）。テストを直した 2 回目（run 39401149642、804d6c1）で成功した（UI テスト 15 分 9 秒）。Azure CLI のサインインで、サブスクリプションの読み込み → 状態の確認 → P1 → P3（「リソースグループを確認しています…」）→ 計画（作成する 5 件）→ デプロイ（約 2 分 10 秒）→ P2 → 状態の確認 → 削除（P4。Environment の削除に約 12 分）→ 削除後の状態の確認、まで通り、リソースグループは片付けで削除した。アプリの診断用のログに、処理されなかった例外は無かった
- 期限切れのデプロイ（No.78、2026-10-29）: テスト中のアプリは、サブスクリプションにある期限切れのデプロイを見つけると、期限での削除のジョブを自動で登録する。2026-10-29 の実行（run 40854228403）では、置き忘れの `tt-test-1 / timetracker` の削除が同時に実行できる数の枠を使い、テストの 2 つ目のデプロイが待機中になって止まった。このサブスクリプションは開発用で、期限切れは消してよい（オーナーの判断）。UI テストの前に `.github/scripts/remove-expired-deployments.ps1` で、期限切れのデプロイ（管理タグの付いたリソースの `tt-expires-at` のうち最も早いものが今より前）を探し、CLI の `delete` で削除する（本ツールで作ったリソースグループが空になれば、リソースグループも削除する。デスクトップアプリの期限での削除と同じ）。探し方は Pester のテスト（`tests/scripts/RemoveExpiredDeployments.Tests.ps1`）で確かめる。2 回目（run 40864735420）は、事前の削除の後、2 つ目のデプロイの停止まで通ったが、削除の完了で一覧を作り直したときに WinUI.TableView の中で例外になり、アプリが終了した（`desktop-tables.md` の 10.3.1 で直した）。3 回目（run 40876707444）は、2 つ目のデプロイを始めた直後に、表で最初に見つかった「状態」が終わったジョブ（完了）で、確かめ方と合わなかった。UI Automation の表の行の順は、画面の行の順と一致するとは限らないため、ジョブの行は位置ではなく対象（リソースグループ / アプリ名）と状態で探すようにした（`AppSession.RowWith`・`RowWhere`・`WaitForRowText`。表の行の文字をログに残す）。4 回目（run 40878628570、8fc250b）で成功した（UI テスト 16 分 11 秒。事前の確認では期限切れのデプロイは無かった）。表の絞り込み（リソースグループの文字）・2 つのデプロイの同時実行・停止・削除・削除後の一覧の作り直しまで通り、リソースグループは片付けで削除した。5 回目（run 40982525937、79f80f8。評価版 0.1.67 の実機の確認で、見出しで並べ替え・▽ が使えないと分かり直した後）で成功した（UI テスト 15 分 14 秒）。ジョブの一覧の「状態」の見出しをセルからたどり、HelpText が「昇順, フィルターをクリア」（並べ替えと ▽ の両方）で、押すたびに名前が「状態, 昇順」→「状態, 降順」→「状態」と変わることを確かめた。6 回目（run 41020015841、49dc690。余白の列・セルのコピーを加えた後）は、2 つ目のデプロイの完了の後、デプロイの一覧を開いたところでアプリが記録を残さずに落ち、一覧の読み込みを待って止まった。行のある表でだけ動く処理（チェックボックスの列の幅が変わったときに余白の列の幅を変える）が、表の行の配置の途中で列の幅を変えていたためとみて、配置の後に変えるようにした（`desktop-tables.md` の 10.6）。CI の UI テストは行の無い表のため、この処理を通らない。ワークフローで、Windows のアプリケーションのイベント（アプリのエラー）を成果物に残すようにした。7 回目（run 41035872850、0694130）も同じところで落ちた。残したイベントは「Application Error」（例外コード `0xc000027b`、`CoreMessagingXP.dll`。WinRT の境界を越えた、処理されない例外）で、余白の列の幅は原因ではなかった。6 回目から加えた、セルの文字を返す変換（`OperationContentBinding`）が、表の読み上げから「切り離された行」の目印を渡され、行の型への変換で例外を出したとみて、行でない値には空を返し、例外を出さないようにした。8 回目（run 41039566120、ff5f9ed）も同じ落ち方（0xc000027b、CoreMessagingXP.dll）で、変換も原因ではなかった。落ちたのは、1 つ目のデプロイの完了の後、「含む文字」でデプロイの一覧が 2 行から 1 行に減った直後（入力が止まってからの絞り込みは DispatcherQueueTimer の Tick の中で一覧を作り直すため、その中の例外は UnhandledException に届かず、CoreMessaging がアプリを止める）。推測での修正をやめ、UI テスト用のビルドだけ、投げられたすべての例外と呼び出し元（FirstChanceException。Azure の応答の誤りは除く）をログに書くようにした。あわせて、どのビルドでも AppDomain の処理されなかった例外と、待たれなかったタスクの例外を書く。9 回目（run 41050364091、346704a）で原因を特定した: 一覧が 2 行から 1 行に減った直後、`TableBinder.SizeSpacer` が `GridLength(Double)` で `ArgumentException`（Parameter 'value'）を投げ、DispatcherQueue の中のためアプリが落ちた。表がチェックボックスの列の幅（`CellsHorizontalOffset`）を一時的に NaN にするためで、NaN のときは余白の幅を変えないようにした（`desktop-tables.md` の 10.6）。7・8 回目の見立て（セルの文字を返す変換）は外れていた
- 制約: GitHub Actions の `workflow_dispatch` は、既定のブランチ（`main`）にあるワークフローしか手動で実行できない。作業ブランチで実行するには、このワークフローのファイルだけを先に `main` に入れる（実行するときに作業ブランチを選ぶと、作業ブランチのワークフローとテストを使う）

## 7. 実施の順序
| フェーズ | 内容 | 完了条件 |
|---|---|---|
| 6a | AutomationId、テストのプロジェクト、CI のテスト（5）、`desktop-ui` ジョブ | CI で全件成功。テストを壊した版（例: 選択の変更イベントを外す）で失敗することを確かめる |
| 6b | ローカルの半自動のテスト（6）とスクリプト、手順（`desktop-app-check.md` に追記） | オーナーの PC で成功（Azure での実行は、実行の前にオーナーの確認を取る） |
| 6c | UI テストの System Test（6.1） | GitHub Actions で成功（実行はオーナーの承認の後） |

**実施結果（6a）**
- `MainWindow.xaml` の 66 の要素に AutomationId を付け、`tests/TTDeployTool.Controller.Desktop.UITests`（7 件）と CI の `desktop-ui` ジョブを追加した。GitHub のホストされた Windows ランナー（Windows Server 2025）で、UI Automation のパターンでの操作、フォルダーの選択のダイアログ（ID 1152・1）の操作、失敗したときのスクリーンショットができることを確かめた（テストは約 12 秒。MSIX のビルドと導入を含むジョブ全体は約 10 分）
- CI で直したこと（アプリ）: 文字列の一覧（`ItemsControl`）の各行が UI Automation の要素にならなかったため、各行を `TextBlock` で表示するテンプレート（`LineTemplate`）にした。エラーの `InfoBar` のメッセージが読めなかったため、`AutomationProperties.Name` にもメッセージを出した（スクリーン リーダーでも読めるようになる）。テキストボックスの値は、入力のたびに ViewModel に戻すようにした（`UpdateSourceTrigger=PropertyChanged`。既定はフォーカスが外れたとき）
- CI で直したこと（テスト）: 名前を持たない要素の `Name` を読むと例外になるため、空として読む。一覧の内容は、ウィンドウの中の文字列から確かめる
- 6b の準備: `AzureUiTests`（`TT_UITEST_SUBSCRIPTION_ID` と `TT_UITEST_RESOURCE_GROUP` が無ければスキップ。CI ではスキップされる）と `scripts/run-desktop-ui-tests.ps1`（実行の前に確認し、終わったらリソースグループを削除するかを聞く）を追加した。手順は [`desktop-app-check.md`](desktop-app-check.md) の 5.1。オーナーの PC での実行は未実施（Azure での実行の前にオーナーの確認を取る）
- テストを壊した版で失敗することの確認: DB 種別の選択を ViewModel に戻すイベントを外した版（一時的なブランチ）で、Dry-run の保存のテストだけが失敗した（7 件中 1 件。SQL Server のイメージの `docker-compose.yml` にならない）。CI の run 39349465420（成功）・39350045306（壊した版で失敗）

## 8. 決定事項
| # | 論点 | 選択肢 | 決定 |
|---|---|---|---|
| Q1 | CI でアプリを起動する方式 | (A) パッケージなし（`WindowsPackageType=None`）の自己完結の exe を起動する。簡単だが、MSIX の登録・パッケージの ID・アプリのデータの置き場所は確かめられない / (B) CI の中で一時的な自己署名の証明書を作り、MSIX に署名して導入し、AppUserModelID で起動する。Store に出すものに近い形で確かめられる。証明書はジョブの中だけで使い、保存しない | **(B)**（推奨のとおり） |
| Q2 | Dry-run の保存（OS のフォルダーの選択のダイアログ）を CI で操作するか | (A) ダイアログも UI Automation で操作し、保存したファイルまで確かめる（OS の版・言語でダイアログの作りが変わると壊れやすい） / (B) CI では、誤った設定でエラーが表示されることと、ダイアログが開いて取り消せることまで。保存はローカルの半自動で確かめる | **(A)**（オーナー判断。ダイアログの要素は、表示の言語によらない ID で探す） |
| Q3 | ローカルの半自動のテストで、Azure へのデプロイと削除まで行うか | (A) 計画まで（何も作らない。料金がかからない） / (B) 実行と削除まで（約 15〜20 分、料金がかかる。スクリプトが毎回、実行の前に確認する） | **(B)**（推奨のとおり） |

## 9. テストを壊さないための決まり
- 待ち合わせは固定の時間ではなく、条件（要素が出る・表示が変わる）で待つ。上限を決め、超えたらスクリーンショットを残して失敗にする
- テストごとにアプリを起動し直す（前のテストの状態を引き継がない）
- 文言ではなく AutomationId で要素を探す。AutomationId を変えるときは、テストも同じ変更で直す

## 9.1 単体テストで確かめる画面の取り決め（No.64、オーナーの依頼 2026-10-26）
No.63 の Desktop System Test の失敗のうち、アプリの側の誤り（絞り込みの「閉じる」に AutomationId が無い）は、XAML と画面の側のコード（MainWindow.xaml.cs・App.xaml.cs）にあり、単体テストが届いていなかった。オーナーの依頼で、次の 2 つを行った（A・B 案）。

- **A. 画面の側の判断を Desktop.Core に移す**: 画面の側は、受け取って渡すだけにする
  - 表（ListView）の選択と ViewModel の選択の受け渡し: `SelectionSync<T>`（表の `SelectedItems` を `Attach` し、`SelectionChanged` で `ViewChanged` を呼ぶ。ViewModel が選び直したら表に戻し、戻している間の表の変更は返さない）。デプロイの一覧・ジョブの一覧の `TableSelection`
  - 見出しの Tag からの並べ替え: `SortBy(string)`（列の名前）
  - 画面の名前: `Pages`（MainViewModel が開く画面、XAML の各画面とメニューの Tag）。画面の切り替えは、`PagesHost` の子のうち Tag が一致するものを出す
  - 起動の種類の振り分け: `MainViewModel.StartAsync(atStartup)`
  - 通知を押したときに開く画面: `MainViewModel.OpenFromNotificationAsync`（引数のキーは `AppNotice.PageArgument`・`JobArgument`）
  - StartupTask の状態の読み替え: `StartupTaskStates.FromWindowsState`（WinRT の型を使わず、名前で受け取る）
- **B. XAML を読む単体テスト（XamlContractTests。Linux でも動く）**: AutomationId がウィンドウと各テンプレートの中で重複しないこと、UI テストが使う AutomationId（`app.Invoke("…")` などの文字列と `Show("X")` の `Nav.X`・`Panel.X`）が XAML にあること、画面とメニューの Tag が `Pages` と一致すること、`x:Bind` の名前が ViewModel（テンプレートの中は `x:DataType` の型）にあること、イベントの処理が MainWindow.xaml.cs にあること、`local:Ui` の関数が Ui.cs にあること。わざと 6 種類の誤り（今回の AutomationId の抜け、名前の誤り、重複、画面の名前、処理の名前、テンプレートの中の名前）を入れて、すべて失敗することを確かめた
- 単体テストで確かめられないもの（見た目、ListView の選択の見え方、通知の表示、通知領域、サインイン時の起動、WAM）は、今までどおり UI テストと実機で確かめる

## 10. 出典（2026-10-19 に確認）
- winapp CLI の UI Automation（パターンを使う命令は CI・ロックされた画面でも動く）: https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/ui-automation 、https://learn.microsoft.com/windows/apps/dev-tools/winapp-cli/guides/winui
- AutomationProperties.AutomationId: https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.automationproperties.automationidproperty
- FlaUI: https://github.com/FlaUI/FlaUI 、https://www.nuget.org/packages/FlaUI.UIA3
- WinAppDriver の状況（2022 年以降更新されていない）: https://appium.io/docs/en/3.0/ecosystem/drivers 、https://learn.microsoft.com/answers/questions/1455246/is-the-tool-winappdriver-dead-or-not
- パッケージの署名に使う証明書の作成: https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing 、https://learn.microsoft.com/windows/msix/package/sign-app-package-using-signtool
- WinUI 3 の CI: https://learn.microsoft.com/windows/apps/package-and-deploy/ci-for-winui3
