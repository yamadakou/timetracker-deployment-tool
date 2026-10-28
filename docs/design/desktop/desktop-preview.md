# デスクトップアプリの評価版の配布（自己署名の MSIX）

Microsoft Store に提出する前に、少数の**評価者**にデスクトップアプリを試してもらうため、自己署名の証明書で署名した MSIX を作って渡す手順の設計です（REVIEW_ISSUES No.65。Store 以外の配布の検討は [`desktop-app.md`](desktop-app.md) の 8.3）。

- ワークフロー: [`.github/workflows/desktop-preview.yml`](../../../.github/workflows/desktop-preview.yml)（手動で実行。手動の実行は既定のブランチにあるワークフローしか使えないため、作業ブランチでは、最後のコミットのメッセージに `[preview]` を入れて push すると作る。Azure は使わない）
- 評価者の手順書: [`docs/DESKTOP_PREVIEW_INSTALL.md`](../../DESKTOP_PREVIEW_INSTALL.md)（成果物にも `INSTALL.md` として入る）
- 一次情報の確認日: 2026-10-26（出典は 6）

## 1. 流れ
1. （初回だけ）オーナーが署名の証明書（PFX）を作り、リポジトリのシークレットに入れる……3
2. オーナーが Actions の **Desktop Preview** → Run workflow で実行する（版は空のままでよい。main に入れる前は、作業ブランチに `[preview]` をメッセージに入れたコミットを push する）
3. 実行結果の Artifacts から `ttdeploytool-desktop-preview-<版>` を取り出し、評価者に渡す（社内のファイル共有など）
4. 評価者が `INSTALL.md` に従って、証明書を信頼してから `.msix` を導入する

## 2. 決定事項
| No. | 内容 | 理由 |
|---|---|---|
| P1 | 証明書は、シークレット `DESKTOP_PREVIEW_PFX`（PFX の Base64）と `DESKTOP_PREVIEW_PFX_PASSWORD` があればそれを使い、無ければその実行だけの証明書を作る（実行に警告を出す） | 同じ証明書なら、評価者は初回に一度信頼するだけで以後の版を導入できる。シークレットが無くても試しに作れるようにする |
| P2 | 証明書の発行先は `CN=TTDeployTool`（マニフェストの `Publisher` の仮の値）。パッケージの `Name` も仮の値のまま | 署名の証明書の Subject はマニフェストの Publisher と一致する必要がある（出典 1）。Store の版は発行元が Partner Center の値になるため、評価版と Store の版は別のアプリとして並べて入る |
| P3 | 評価者は証明書を「ローカル コンピューター」の**信頼されたユーザー**（Trusted People）に入れる | MSIX の手順（出典 1）が求める保存場所。サイドロードの一般の手順（出典 2）は「信頼されたルート証明機関」を挙げるが、ルートに入れると信頼の範囲が広がるため、必要な最小限にする |
| P4 | x64 と arm64 の `.msix` を別々に作る（バンドルにしない） | 評価者は自分の PC の 1 つだけを受け取ればよく、ダウンロードが半分で済む。CI の UI テストと同じ作り方（`/p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly`）を使える |
| P5 | 版は入力の X.Y.Z（空なら `0.1.<実行番号>`）から X.Y.Z.0 にする | MSIX は同じか古い版では上書きできないため、実行のたびに大きくする。ローカルのビルド（0.1.0.0）より大きい |
| P6 | 成果物は Actions の Artifacts に 30 日だけ置き、GitHub Release には載せない | 評価者だけに渡すもので、一般の利用者には勧めない（8.3 の Q2 の決定のまま） |
| P7 | 作った x64 の MSIX を、評価者と同じ手順（`.cer` を信頼 → `Add-AppxPackage`）で runner に導入し、版を確かめてから削除する | 署名と証明書の組み合わせの誤りを、評価者に渡す前に見つける |
| P8 | サインインのクライアント ID は、リリースと同じリポジトリの変数 `TT_DESKTOP_CLIENT_ID` を渡す（未設定なら Azure.Identity の既定） | Store の版と同じサインインを評価できる |

## 3. 署名の証明書を作る（オーナー・初回だけ）
Windows の PowerShell で行います（証明書の作り方は出典 1）。

```powershell
# 1. 署名の証明書を作る（発行先は CN=TTDeployTool。期限は 2 年）
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=TTDeployTool" -KeyUsage DigitalSignature `
  -FriendlyName "TTDeployTool preview" -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(2) `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

# 2. パスワードを付けて PFX に書き出す
$password = Read-Host -AsSecureString "PFX のパスワード"
Export-PfxCertificate -Cert $cert -FilePath .\TTDeployTool-preview.pfx -Password $password

# 3. Base64 にしてクリップボードに入れる
[Convert]::ToBase64String([IO.File]::ReadAllBytes(".\TTDeployTool-preview.pfx")) | Set-Clipboard
```

4. GitHub のリポジトリに、**リポジトリのシークレット**として次の 2 つを追加します。
   1. リポジトリの上のタブの **Settings** を開き、左のメニューの「Security and quality」の下の **Secrets and variables** を開いて **Actions** を選びます（「Actions secrets and variables」の画面が開きます）。
   2. **Secrets** のタブのまま、「**Repository secrets**」の右の緑の **New repository secret** を押します（上の「Environment secrets」の **Manage environment secrets** ではありません。このワークフローは環境〔`systemtest` など〕を使わないため、環境のシークレットは読めません）。
   3. **Name** に `DESKTOP_PREVIEW_PFX`、**Secret** に 3 でコピーした Base64（数千文字の 1 行）を貼り付け、**Add secret** を押します。
   4. もう一度 **New repository secret** を押し、**Name** に `DESKTOP_PREVIEW_PFX_PASSWORD`、**Secret** に 2 のパスワードを入れて **Add secret** を押します。
   5. 「Repository secrets」の一覧に、既存の `TT_E2E_*` と並んで 2 つが出れば完了です（値は後から見られません。間違えたら鉛筆のボタンで入れ直します）。

   GitHub CLI（`gh`）を使う場合は、PFX のあるフォルダーで次を実行しても同じです（パスワードは尋ねられたときに入力します）。

   ```powershell
   [Convert]::ToBase64String([IO.File]::ReadAllBytes(".\TTDeployTool-preview.pfx")) | gh secret set DESKTOP_PREVIEW_PFX -R yamadakou/timetracker-deployment-tool
   gh secret set DESKTOP_PREVIEW_PFX_PASSWORD -R yamadakou/timetracker-deployment-tool
   ```
5. PFX は、パスワード管理の仕組みなど安全な場所に保管するか、削除します。証明書の秘密鍵を持つ人は、評価者の PC で信頼されるパッケージを作れるためです。PC の証明書のストアからも削除します（`Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"`）。

### 期限と作り直し
- ワークフローは、証明書の期限が 30 日を切っていると失敗します。署名に時刻印を付けていないため、期限を過ぎた証明書で署名したパッケージは導入できなくなります。期限の前に 3 をやり直し、シークレットを置き換えて配布し直します（評価者は新しい `.cer` を信頼し直します）。
- シークレットが無いときに作るその実行だけの証明書は、期限が 90 日です。

## 4. 成果物の中身
```
ttdeploytool-desktop-preview-<版>/
  INSTALL.md                         評価者の手順書（docs/DESKTOP_PREVIEW_INSTALL.md）
  TTDeployTool-preview.cer           署名の証明書（公開鍵だけ）
  x64/TTDeployTool-<版>-x64.msix     （と、あれば Dependencies/）
  arm64/TTDeployTool-<版>-arm64.msix （と、あれば Dependencies/）
```
アプリは自己完結（.NET と Windows App SDK を含む。`desktop-app.md` の 8.1）のため、通常は Dependencies はありません。

## 5. 注意
- 評価者の PC が会社のポリシーでサイドロードを禁止していると導入できません（出典 2）。その場合は PC の管理者に相談してもらいます。
- 評価版は、Visual Studio などで開発用に導入したアプリ（同じ `Name` と `Publisher`）とは同じアプリとして扱われ、開発用のものが入っていると導入できません（0x80073CFB「パッケージ化されていないバージョンを既にインストールしています」。オーナーの PC で 2026-10-26 に発生）。`Get-AppxPackage -Name TTDeployTool | Remove-AppxPackage` で開発用のものを削除してから導入します（評価者の手順書の「導入できないとき」）。
- 評価版は、Store の版と別のアプリとして入ります（P2）。Store の版を入れた後は、評価版をアンインストールしてもらいます。
- サインイン時の起動（StartupTask、`desktop-jobs.md` の 6.5）は、評価版でも Store の版と同じく使えます（パッケージのマニフェストの機能のため）。

## 5.1 作った評価版
| 版 | 作った日 | 主な中身 |
|---|---|---|
| 0.1.1 | 2026-10-26 | 最初の評価版（No.65） |
| 0.1.2 | 2026-10-27 | 削除の「予定に追加」（No.66）、Windows との連携・削除の予定を戻す（No.67） |
| 0.1.3 | 2026-10-27 | 「ジョブを見る」を「状態を確認」にまとめる（No.69） |
| 0.1.5 | 2026-10-27 | 登録した後の「新規デプロイ」「続けてデプロイ」（No.70） |
| 0.1.7 | 2026-10-27 | ウィザードの完了の表示（No.71） |
| 0.1.19 | 2026-10-27 | デプロイの予定をパスワードを暗号化して残す（No.72）、デプロイの一覧の「状態」（No.73）（Desktop Preview の run 40497100979） |
| 0.1.27 | 2026-10-27 | 押せない理由の表示（No.74）、件数の表示（No.75）、終わったジョブの記録・「Azure から削除…」「記録から削除」・ジョブの一覧の日時の列（No.76）（Desktop Preview の run 40536978194） |
| （作成中） | 2026-10-28 | デプロイのグループ（No.77）と、詳細設計の文書の整理・アプリのデータのフォルダーの説明 |

（実行番号で版が決まるため、`[preview]` の無い push で飛ばした実行の分だけ版が飛ぶ）

## 6. 出典（2026-10-26 に確認）
1. [Create a certificate for package signing - MSIX](https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing): 証明書の Subject はマニフェストの Publisher と一致する必要がある。`New-SelfSignedCertificate` の KeyUsage・EKU（1.3.6.1.5.5.7.3.3）・Basic Constraints。導入する PC では「ローカル コンピューター」の Trusted People に入れる。不要になったら削除する
2. [Sideload line of business apps](https://learn.microsoft.com/windows/application-management/sideload-apps-in-windows): ストア以外のパッケージの導入（サイドロード）。組織のポリシーで禁止されていると、利用者は有効にできない。`.msix` をエクスプローラーで開くか `Add-AppxPackage` で導入する
