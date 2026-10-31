# Blazor Hybrid の試作の実機での確認（Windows の通知など）

- 目的: 移行で機能が落ちないか。CI のランナー（Windows Server 2025）には通知を画面に出す仕組みが無く、通知を出して押す流れを確かめられなかった（[`desktop-blazor-hybrid.md`](desktop-blazor-hybrid.md) の 15.1）。オーナーの PC で、試作（WPF の BlazorWebView のホスト）の通知が出るか・押すとウィンドウが出るかを確かめる
- 対象: CI（`ci.yml` の `blazor-prototype-windows` ジョブ）の成果物 `ttdeploytool-blazor-prototype-msix`（7 日で消える）。評価版（Desktop Preview）とは別のアプリ（`TTDeployTool.BlazorPrototype`）で、今の TTDeployTool と並べて入る
- 試作のデータは Fixtures のモックだけで、サインインせず、Azure には届かない
- 記録: `docs/REVIEW_ISSUES.md` の No.79
- **済み（2026-10-31。すべて OK）**: この手順は、試作の MSIX（run 41147397707）のためのもの。確認の後、WinUI 3 版と同じ機能を実装したため（設計書の 16 章）、通知領域の確認のメニュー（`ManualCheck`）は外し、成果物の MSIX は本番の部品（サインインは WAM・Azure は本物）になった。この手順は記録として残す

## 1. 受け取るもの
CI の実行の **Summary** の下の **Artifacts** から `ttdeploytool-blazor-prototype-msix` を取り出し、展開する。

| ファイル | 内容 |
|---|---|
| `TTDeployTool-BlazorPrototype-x64.msix` | 試作のアプリ（x64 だけ。arm64 の PC では確かめられない） |
| `TTDeployTool-BlazorPrototype.cer` | その実行だけで作った署名の証明書（公開鍵だけ。秘密鍵は成果物に入れていない） |
| `CHECK.md` | この手順書 |

## 2. 入れる
1. スタート メニューで「PowerShell」を右クリックし、「**管理者として実行**」で開く。
2. 証明書を「ローカル コンピューター」の**信頼されたユーザー**に入れる（評価版の手順〔`docs/DESKTOP_PREVIEW_INSTALL.md`〕と同じ場所。パスは置き換える）。

   ```powershell
   Import-Certificate -FilePath "C:\Users\<名前>\Downloads\ttdeploytool-blazor-prototype-msix\TTDeployTool-BlazorPrototype.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```
3. 管理者でない PowerShell で、アプリを入れる。

   ```powershell
   Add-AppxPackage -Path "C:\Users\<名前>\Downloads\ttdeploytool-blazor-prototype-msix\TTDeployTool-BlazorPrototype-x64.msix"
   ```
4. スタート メニューの「**TTDeployTool（Blazor Hybrid の試作）**」で起動する。モックのデプロイの一覧（45 件、20 行ずつ）が出る。

## 3. 確かめること（Windows 11）
### 3.0 準備（通知が止められていないか）
1. **設定**（Win+I）→ **システム** → **通知** を開く。
2. 一番上の「**通知**」がオンで、「**応答不可**」（Do not disturb）がオフになっていることを確かめる（応答不可がオンだと、通知はバナーで出ずに通知センターにだけ入る）。
3. 試作の通知を一度も出していないうちは、下の「アプリやその他の送信者からの通知」の一覧に試作はまだ出ない（3.2 で出した後に出る）。

### 3.1 通知領域のアイコンを探す
1. 試作を起動すると、タスク バーの右端（時計の左）の通知領域に、TTDeployTool のアイコンが出る。マウスを置くと「**TTDeployTool（Blazor Hybrid の試作）**」と出る（今の TTDeployTool を動かしていれば、そのアイコンは「TTDeployTool（ジョブはありません）」などと出るので、それと区別する）。
2. 見えないときは、通知領域の **^**（「隠れているインジケーターを表示」）を押すと、隠れているアイコンの中にある。
3. いつも見えるようにするには、**設定** → **個人用設定** → **タスク バー** → 「**その他のシステム トレイ アイコン**」を開き、試作（「TTDeployTool（Blazor Hybrid の試作）」または「ttdeploytool-blazor」）をオンにする（任意）。
4. アイコンを**右クリック**すると、次のメニューが出る。

   ```
   開く
   通知を試す（今のアプリと同じ: Windows App SDK）
   通知を試す（代わり: OS の通知の API）
   サインイン時の起動を切り替える
   状態を見る
   ─────────
   終了
   ```
   アイコンを左クリックすると、ウィンドウが出る（「開く」と同じ）。

### 3.2 確かめる操作
各項目の結果（✔ / ✘ と、出た文字）を記録する。

| # | 操作 | 確かめること |
|---|---|---|
| 1 | アイコンを右クリック →「**状態を見る**」 | 「TTDeployTool（試作）」の小さな画面が出る。次の 2 行の値を記録して「OK」で閉じる: 「通知の許可（今のアプリと同じ: Windows App SDK）: 〇〇」「通知の許可（OS の通知の API）: 〇〇」（Enabled なら許可。ほかの行もあれば写す） |
| 2 | 試作のウィンドウの右上の **×** を押す | ウィンドウが消え、アイコンは通知領域に残る（終了しない） |
| 3 | アイコンを右クリック →「**通知を試す（今のアプリと同じ: Windows App SDK）**」 | 画面の右下に、通知のバナー「**試作の通知（Windows App SDK）**」「この通知を押すと、試作のウィンドウが出ます。」が数秒出る |
| 4 | 3 のバナーを押す。バナーが消えていたら、タスク バーの**日付と時刻**を押す（または Win+N）と開く**通知センター**で、その通知を押す | 試作のウィンドウが出て、「**通知から開きました（Windows App SDK）。job=job-manual&page=status**」の小さな画面が出る。「OK」で閉じる |
| 5 | もう一度 × でウィンドウを隠し、アイコンを右クリック →「**通知を試す（代わり: OS の通知の API）**」→ 出た通知「**試作の通知（OS の通知の API）**」を押す | ウィンドウが出て、「**通知から開きました（OS の通知の API）。**」が出る（5 は、3・4 が駄目だったときの代わりの作りを確かめるもの） |
| 6 | アイコンを右クリック →「**状態を見る**」 | 「押された通知（Windows App SDK）: job=job-manual&page=status」になっている。「通知の許可」の 2 行が 1 と同じか |
| 7（任意） | アイコンを右クリック →「通知を試す（今のアプリと同じ: Windows App SDK）」を押した直後に、メニューの「**終了**」で試作を終える（アイコンが消える）→ 通知センター（Win+N）で、その通知を押す | 試作が起動してウィンドウが出る（今のアプリと同じく、終わった後に押した通知は普通の起動になり、「通知から開きました」は出ないことがある） |
| 8（任意） | アイコンを右クリック →「**サインイン時の起動を切り替える**」（「サインイン時の起動: Disabled → Enabled」が出る。初めてのときは、Windows が許可を尋ねる画面を出すことがある）→ スタート →（アカウントのアイコン）→ **サインアウト**し、サインインし直す | サインインの後に、ウィンドウを出さずに試作のアイコンが通知領域に出る、またはウィンドウが出る。アイコンを右クリック →「状態を見る」で「起動の種類: StartupTask」。確かめたら、もう一度「サインイン時の起動を切り替える」で「Enabled → Disabled」に戻す |
| 9（任意） | 試作を動かしたまま、スタート メニューの「TTDeployTool（Blazor Hybrid の試作）」をもう一度押す | 2 つ目は開かず、今の試作のウィンドウが前に出る |
| 10（任意） | 試作のウィンドウの「含む文字」に「えいぎょう」と打ち、変換の途中（下線がある状態）で 1 秒ほど待つ。その後 Enter で「営業」に確定する | 変換の途中では表が変わらず、文字も消えない。確定の少し後に「絞り込んだ 15 件」になり、そのまま続けて文字を打てる |

- 8 は、試作がサインイン時に起動した後、試作を終了してから確かめを続ける。8 の後に一度サインアウトするため、記録は先に書いておく
- 4・5 で「通知から開きました」が出ない・通知が出ない場合は、3.0 の設定（通知・応答不可）と、**設定** → **システム** → **通知** の一覧の「TTDeployTool（Blazor Hybrid の試作）」がオンかを確かめ、もう一度試す

## 4. 片付け
1. 試作を終了する（通知領域のメニューの「終了」）。
2. 試作を外す（管理者でない PowerShell）。

   ```powershell
   Get-AppxPackage -Name TTDeployTool.BlazorPrototype | Remove-AppxPackage
   ```
3. 証明書を外す（管理者の PowerShell。発行先は評価版と同じ `CN=TTDeployTool` のため、拇印で選ぶ）。

   ```powershell
   $cer = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 "C:\Users\<名前>\Downloads\ttdeploytool-blazor-prototype-msix\TTDeployTool-BlazorPrototype.cer"
   Remove-Item "Cert:\LocalMachine\TrustedPeople\$($cer.Thumbprint)"
   ```
4. 試作のデータのフォルダー（`%LOCALAPPDATA%\Packages\TTDeployTool.BlazorPrototype_<発行元のハッシュ>`）は、2 でアプリと一緒に消える。

## 5. 結果の見方
| 結果 | 意味と次の作業 |
|---|---|
| 2・3 が ✔ | 今の通知の実装（Windows App SDK）は、WPF のホストでもそのまま使える。CI での `Unsupported` は、ランナーに通知の仕組みが無いため |
| 2・3 が ✘、4 が ✔ | WPF のホストでは Windows App SDK の通知が使えない。移るときは、通知を OS の通知の API で作り直す（押したときの起動の受け口を自分で書く） |
| 2・3・4 がどれも ✘ | Windows の設定（システム → 通知）で、試作の通知が止められていないかを見る。止められていなければ、移る前に原因を調べる |
