<#
.SYNOPSIS
  デスクトップアプリの Store 用のパッケージ（.msixupload）の中のマニフェストを確かめる（docs/design/desktop/desktop-app.md の 8.1・9.1）。

.DESCRIPTION
  .msixupload → .msixbundle → 各アーキテクチャの .msix の順に展開し、それぞれの AppxManifest.xml で次を確かめる。
  - 版が ExpectedVersion と同じ（Store では 4 つ目を 0 にする必要がある）
  - 対象 OS が Windows.Desktop の 10.0.17763.0（Windows 10 1809）以降
  - 機能が internetClient と runFullTrust だけ
  - x64 と arm64 の両方がある
  Store への提出の前に、パッケージの設定の誤りを見つけるため。

.EXAMPLE
  ./.github/scripts/check-msix.ps1 -PackageDir AppPackages -ExpectedVersion 1.2.3.0
#>
#Requires -Version 7
param(
    [Parameter(Mandatory)] [string] $PackageDir,
    [Parameter(Mandatory)] [string] $ExpectedVersion
)
$ErrorActionPreference = "Stop"

# MSIX とバンドルは zip の形式（Expand-Archive は拡張子 .zip を求めるため、複製してから展開する）
function Expand-Package([string] $Path, [string] $Destination) {
    $zip = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName() + ".zip")
    Copy-Item $Path $zip
    Expand-Archive $zip -DestinationPath $Destination -Force
    Remove-Item $zip
}

$uploads = @(Get-ChildItem $PackageDir -Recurse -Filter *.msixupload)
if ($uploads.Count -ne 1) { throw ".msixupload が 1 つではありません（$($uploads.Count) 個）。" }
$work = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
Expand-Package $uploads[0].FullName (Join-Path $work "upload")

$bundles = @(Get-ChildItem (Join-Path $work "upload") -Filter *.msixbundle)
if ($bundles.Count -ne 1) { throw ".msixupload の中に .msixbundle が 1 つではありません（$($bundles.Count) 個）。" }
Expand-Package $bundles[0].FullName (Join-Path $work "bundle")

$packages = @(Get-ChildItem (Join-Path $work "bundle") -Filter *.msix)
$architectures = @()
foreach ($package in $packages) {
    $dir = Join-Path $work $package.BaseName
    Expand-Package $package.FullName $dir
    [xml] $manifest = Get-Content (Join-Path $dir "AppxManifest.xml")
    $identity = $manifest.Package.Identity
    if ($identity.Version -ne $ExpectedVersion) { throw "$($package.Name): 版が $($identity.Version) です（期待: $ExpectedVersion）。" }
    $family = $manifest.Package.Dependencies.TargetDeviceFamily
    if ($family.Name -ne "Windows.Desktop" -or [version] $family.MinVersion -lt [version] "10.0.17763.0") {
        throw "$($package.Name): 対象 OS が $($family.Name) $($family.MinVersion) です（期待: Windows.Desktop 10.0.17763.0 以降）。"
    }
    $capabilities = @($manifest.Package.Capabilities.ChildNodes | ForEach-Object { $_.GetAttribute("Name") } | Sort-Object)
    if (($capabilities -join ",") -ne "internetClient,runFullTrust") { throw "$($package.Name): 機能が $($capabilities -join ', ') です（期待: internetClient, runFullTrust）。" }
    $architectures += $identity.ProcessorArchitecture
    Write-Host "$($package.Name): $($identity.Name) $($identity.Version) $($identity.ProcessorArchitecture) / $($family.MinVersion) / $($capabilities -join ', ')"
}
if ((($architectures | Sort-Object) -join ",") -ne "arm64,x64") { throw "アーキテクチャが $($architectures -join ', ') です（期待: arm64, x64）。" }
Write-Host "パッケージのマニフェストを確かめました。"
