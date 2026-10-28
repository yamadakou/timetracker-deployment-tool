# 仕様の検討: デプロイの一覧・ジョブの一覧の表の使い勝手（No.78）

- 対象ブランチ: `feature/desktop-app`
- 関連: [`desktop-wizard.md`](desktop-wizard.md) の 7（表。Q6・Q9 で WinUI 3 の ListView で作ると決めた）・[`desktop-jobs.md`](desktop-jobs.md) の 6.8（ジョブの一覧の日時の列）
- 状況: 検討（オーナーの採用の判断待ち）。REVIEW_ISSUES No.78
- 一次情報の確認日: 2026-10-28（出典は 7）

オーナーの実機の確認（2026-10-28、評価版 0.1.39）で挙がった 4 つの点について、仕様の案と、使う人から見たメリット・デメリットを並べる。採用するかは、オーナーの判断を待つ。

## 1. 今の作り
| 項目 | デプロイの一覧 | ジョブの一覧 |
|---|---|---|
| 表 | ListView（行の左にチェックボックス）と、見出しの Grid。列の幅は固定（200・150・130・130・140・140・130・残り） | 同じ。列の幅は固定（110・200・170・120×5・90・残り） |
| 画面の幅 | 画面の区画に `MaxWidth="1240"`・左寄せ | 区画に `MaxWidth="1600"`、表は `Width="1500"` に固定し、横のスクロールの中に置く |
| 横に広げたとき | 1240 より広くならず、右に空白が出る | 1500 より広くならず、右に空白が出る |
| 「すべて」のチェックボックス | 無い | ある（No.76） |
| ページ | 20・50・100 件ずつ | ページなし |

右に空白が出るのは、どちらも幅の上限（`MaxWidth`・`Width`）と左寄せのためである。

## 2. 表をウィンドウの幅に合わせる（論点 Q1）
| 案 | 内容 | メリット | デメリット |
|---|---|---|---|
| **A（推奨）** | 表の区画の幅の上限を外し、最後の列（デプロイの一覧は「ジョブ」、ジョブの一覧は「結果」）を残りの幅いっぱいに伸ばす（`*`）。ほかの列は今の幅のまま。ウィンドウが表の最小の幅より狭いときは、表だけを横にスクロールする（列の幅は縮めない）。表の上の入力欄・ボタンは今の幅のまま | 広い画面で、長い結果・ジョブの文が折り返さずに読める。右の空白が無くなる。狭い画面では今と同じく横にスクロールでき、列の幅が変わらないので見出しと値がずれない | 横に長いモニターでは、最後の列の文が 1 行で長くなり、目で追いにくい（折り返しは残すので、行が増えることはない） |
| B | すべての列を、ウィンドウの幅に合わせて比例で伸ばす | 列の間隔が均等に広がる | 日付・状態など短い列まで広がり、間延びする。狭いときは列が縮んで文字が切れる（「はみ出るときは列幅を調整しない」の依頼と合わない） |
| C | 今のまま | 変更なし | 右の空白が残る |

## 3. ジョブの一覧にサブスクリプションの列（論点 Q2）
| 案 | 内容 | メリット | デメリット |
|---|---|---|---|
| **A（推奨）** | 「リソースグループ / アプリ名」の左に「サブスクリプション」の列を足す（デプロイの一覧と同じく、名前（ID）。前回までのアプリの記録で名前が分からなければ ID）。並べ替えと、▽ での絞り込み（「すべて」と、ジョブにあるサブスクリプション）を付ける | 別のサブスクリプションの同じリソースグループ・アプリ名を区別できる。デプロイの一覧と見方がそろう | 1 つのサブスクリプションしか使わない人には、毎回同じ値の列が増える（Q3 で列を隠せれば解消） |
| B | 列は足さず、「詳細」にだけサブスクリプションを出す | 表が広がらない | 表で区別できない |

## 4. 列の操作（論点 Q3）
依頼の 5 つ: 列の幅の変更・列の順序の変更（チェックの列は左端に固定）・列の表示 / 非表示（チェックの列は必ず表示）・固定の列（チェックの列は左端に固定し、複数の列を固定できる）・はみ出るときは列の幅を縮めず横のスクロール。

### 4.1 共通の決まり（どの案でも）
- 列の設定（幅・順序・表示・固定の数）は、表ごとに、アプリのデータの `preferences.json` に保存し、次の起動でも同じにする。表の上に「列の設定を初期値に戻す」を置く
- チェックの列（行の選択）は、いつも左端で、隠せない・動かせない・幅を変えられない
- 並べ替え（見出しを押す）と絞り込み（▽）は、今の決まり（Core の `DeploymentListViewModel`・`JobListViewModel`）のまま使う

### 4.2 作り方の案
| 案 | 内容 | メリット | デメリット |
|---|---|---|---|
| **A（推奨。ただし先に試作で確かめる）** | 表を、WinUI 3 向けの OSS の表のコントロール WinUI.TableView（MIT。1.5.1、2026-10-14 公開。出典 1〜5）に置き換える。幅の変更（見出しの境目をドラッグ。固定・`*`・自動）、順序の変更（見出しのドラッグ。列ごとに禁止できる）、左からの固定（`FrozenColumnCount`）、「すべて選ぶ」（`CornerButtonMode="SelectAll"`）がそろっている。並べ替えは `Sorting` イベントで今の Core の決まりを使い、内蔵の絞り込みは使わない。表示 / 非表示は、列の設定のメニュー（チェックボックスの一覧）を作る | 依頼の 5 つのほとんどが、使い慣れた表計算に近い操作（境目のドラッグ・見出しのドラッグ）でできる。自作より不具合が少なく、早い | 個人の OSS への依存（スター 370）で、作者が保守をやめると WinUI の更新に追従できないおそれがある。今の表（見出し・行の XAML、UI テストの探し方）を作り直す。内蔵の並べ替え・絞り込みとの二重を避ける調整が要る。今の Windows App SDK 2.5.1 と組み合わせられるか（依存は `Microsoft.WindowsAppSDK.WinUI` 1.8 以上）、UI Automation で UI テストが行を読めるか、列の非表示の API を、試作で確かめる |
| B | 今の ListView のまま自作する。列の定義を Core に持ち、見出しに幅を変えるつまみ、列の設定の画面で順序（上へ・下へ）と表示を変える。固定の列は、固定の部分と流れる部分の 2 つの表を、縦のスクロールを合わせて並べる | 新しい依存が無い。決まりを Core に置けるので、単体テストで確かめやすい | 作る量が多い（特に固定の列と、見出しのドラッグでの順序の変更）。行の高さが違うと、固定の部分と流れる部分がずれるおそれがある |
| C | 段階的に: まず「幅の変更（つまみ）」「表示 / 非表示と順序（列の設定の画面。上へ・下へ）」「横のスクロール」を自作し、固定の列は見送る | 依存が無く、早く出せる。固定の列以外の不便は解消する | 列が多いジョブの一覧で、横にスクロールすると対象（アプリ名）が見えなくなる不便が残る |
| D | 見送る（Q1 だけ行う） | 変更が小さい | 依頼の不便が残る |

## 5. デプロイの一覧の「すべて」のチェックボックス（論点 Q4）
| 案 | 内容 | メリット | デメリット |
|---|---|---|---|
| **A（推奨）** | ジョブの一覧と同じ形で、表の上に「すべて」（今のページの行をすべて選ぶ・外す。一部だけなら中間の表示） | 見えている行だけを選ぶので、見えない行を誤って「Azure から削除」の対象にしない。ジョブの一覧と同じ操作 | 絞り込んだ結果が複数ページのときは、ページごとに選ぶ（ページの大きさを 100 にすれば一度に選べる） |
| B | 絞り込んだすべての行（ほかのページを含む）を選ぶ | 一度に全部を選べる | 見えない行まで選ばれ、まとめての削除・期限やグループの変更で、確かめにくい。ページをまたいで選択を保つ作りに変える必要がある |

## 6. 進め方（採用されたとき）
1. Q3 で A を選んだ場合は、先に試作（半日ほど）で、Windows App SDK 2.5.1 との組み合わせ・UI テスト・列の非表示を確かめ、結果を報告してから置き換える。試作で問題があれば、B か C に切り替えるかを改めて確かめる
2. Q1・Q2・Q4 は、Q3 と別に先に出せる（Q3 で表を作り直す場合は、同じ変更でまとめる）

## 7. 出典（2026-10-28 に確認）
1. [WinUI.TableView（GitHub）](https://github.com/w-ahmad/WinUI.TableView): MIT。WinUI 3 と Uno Platform。列の並べ替え（"Allows users to drag and rearrange columns at runtime"）・固定の列（"Keeps specified number of columns fixed in place while horizontally scrolling"）・並べ替え・Excel 風の絞り込み
2. [WinUI.TableView（NuGet）](https://www.nuget.org/packages/WinUI.TableView): 1.5.1（2026-10-14）。.NET 8〜10、`net*-windows10.0.19041`。依存は `Microsoft.WindowsAppSDK.WinUI` 1.8.260528001 以上
3. [Column sizing](http://w-ahmad.dev/WinUI.TableView/docs/column-sizing.html): `Width`（固定・`Auto`・`*`）、`MinWidth`・`MaxWidth`、`CanResizeColumns`（既定で有効）・列ごとの `CanResize`。`*` は表に幅が決まっているときだけ効く
4. [Frozen columns](http://w-ahmad.dev/WinUI.TableView/docs/frozen-columns.html)・[Column reordering](http://w-ahmad.dev/WinUI.TableView/docs/column-reordering.html): `FrozenColumnCount`（左から。行ヘッダーは別に常に固定）、固定の境目をまたいでドラッグで動かせない。`CanReorderColumns`・列ごとの `CanReorder`・`Order`、`ColumnReordering`（取り消せる）・`ColumnReordered`。列の非表示の API は、この文書には無い（試作で確かめる）
5. [Selection](http://w-ahmad.dev/WinUI.TableView/docs/selection.html)・[Sorting](http://w-ahmad.dev/WinUI.TableView/docs/sorting.html): `SelectionMode`（`Multiple`・`Extended`）、`CornerButtonMode="SelectAll"`、行の選択は ListView と同じ。`Sorting` イベントで `e.Handled = true` にすると内蔵の並べ替えをしない。`CanSortColumns`・列ごとの `CanSort`
6. [DataGrid（Windows Community Toolkit）](https://learn.microsoft.com/windows/communitytoolkit/controls/datagrid): "The DataGrid control is not part of the WinUI 3 controls available in the Windows Community Toolkit version 8.0 and later yet."（WinUI 3 向けの Microsoft の DataGrid は無い）
7. 今の実装: `src/TTDeployTool.Controller.Desktop/MainWindow.xaml`（`DeploymentsPanel` の `MaxWidth="1240"`、`JobsPanel` の表の `Width="1500"`）
