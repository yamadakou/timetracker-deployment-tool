<#
.SYNOPSIS
  デスクトップアプリのパッケージのマニフェスト（Package.appxmanifest）に、リリースの版と Store の ID を設定する
  （docs/design/desktop-app.md の 8.1・9.2）。

.DESCRIPTION
  - Version: タグの版 X.Y.Z から X.Y.Z.0 にする（Store では 4 つ目を 0 にする必要がある）
  - Name / Publisher / PublisherDisplayName: Partner Center でアプリ名を予約したときの値（「製品 ID」の「パッケージ ID」）。
    空なら、マニフェストの今の値（仮の値）のまま。release.yml ではリポジトリの変数から渡す
  Store に出すパッケージの ID は、Partner Center の値と一致しないと受け付けられないため、ビルドの前に設定する。

.EXAMPLE
  ./.github/scripts/set-msix-identity.ps1 -Manifest src/TTDeployTool.Controller.Desktop/Package.appxmanifest -Tag v1.2.3 `
    -Name "12345Contoso.TTDeployTool" -Publisher "CN=00000000-0000-0000-0000-000000000000" -PublisherDisplayName "Contoso"
#>
#Requires -Version 7
param(
    [Parameter(Mandatory)] [string] $Manifest,
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Name,
    [string] $Publisher,
    [string] $PublisherDisplayName
)
$ErrorActionPreference = "Stop"

if ($Tag -notmatch '^v(\d+)\.(\d+)\.(\d+)$') { throw "タグ '$Tag' は vX.Y.Z の形ではありません。" }
$version = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

$path = (Resolve-Path $Manifest).Path
[xml] $xml = Get-Content $path -Raw
$identity = $xml.Package.Identity
$identity.Version = $version
if ($Name) { $identity.Name = $Name }
if ($Publisher) {
    if ($Publisher -notmatch '^CN=') { throw "Publisher '$Publisher' は CN= で始まる値にしてください（Partner Center の「パッケージ/ID/発行元」）。" }
    $identity.Publisher = $Publisher
}
if ($PublisherDisplayName) { $xml.Package.Properties.PublisherDisplayName = $PublisherDisplayName }
$xml.Save($path)
Write-Host "パッケージの ID: $($identity.Name) / $($identity.Publisher) / $version / $($xml.Package.Properties.PublisherDisplayName)"
