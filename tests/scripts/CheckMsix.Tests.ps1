<#
  .github/scripts/check-msix.ps1 のテスト（Pester 5 以上）。
  一時ディレクトリに、マニフェストだけを入れた .msix・.msixbundle・.msixupload（どれも zip の形式）を作って確かめる。

  実行: Invoke-Pester ./tests/scripts
#>

BeforeAll {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:Check = Join-Path $repo ".github/scripts/check-msix.ps1"
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    function script:New-Manifest([string] $Arch, [string] $Version = "1.2.3.0", [string] $MinVersion = "10.0.17763.0",
                                 [string] $Family = "Windows.Desktop", [string[]] $Capabilities = @("internetClient", "runFullTrust")) {
        $caps = ($Capabilities | ForEach-Object {
            if ($_ -eq "runFullTrust") { "<rescap:Capability Name=""$_"" />" } else { "<Capability Name=""$_"" />" }
        }) -join ""
        return @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities">
  <Identity Name="Timetracker.DeploymentTool" Publisher="CN=Test" Version="$Version" ProcessorArchitecture="$Arch" />
  <Dependencies><TargetDeviceFamily Name="$Family" MinVersion="$MinVersion" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Capabilities>$caps</Capabilities>
</Package>
"@
    }

    # $Entries: zip の中の名前 → 内容（文字列）またはファイルのパス（FileInfo）
    function script:New-Zip([string] $Path, [hashtable] $Entries) {
        $zip = [System.IO.Compression.ZipFile]::Open($Path, "Create")
        try {
            foreach ($name in $Entries.Keys) {
                if ($Entries[$name] -is [System.IO.FileInfo]) {
                    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Entries[$name].FullName, $name) | Out-Null
                } else {
                    $writer = [System.IO.StreamWriter]::new($zip.CreateEntry($name).Open())
                    $writer.Write($Entries[$name]); $writer.Dispose()
                }
            }
        } finally { $zip.Dispose() }
        return Get-Item $Path
    }

    # 各アーキテクチャの .msix（$Manifests: アーキテクチャ → マニフェスト）を .msixbundle に、それを .msixupload に入れる
    function script:New-Upload([hashtable] $Manifests, [int] $Bundles = 1, [int] $Uploads = 1) {
        $dir = Join-Path $script:Work "AppPackages"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $msix = @{}
        foreach ($arch in $Manifests.Keys) {
            $msix["app_$arch.msix"] = New-Zip (Join-Path $script:Work "app_$arch.msix") @{ "AppxManifest.xml" = $Manifests[$arch] }
        }
        $bundle = New-Zip (Join-Path $script:Work "app.msixbundle") $msix
        $inUpload = @{}
        for ($i = 0; $i -lt $Bundles; $i++) { $inUpload["app$i.msixbundle"] = $bundle }
        for ($i = 0; $i -lt $Uploads; $i++) { New-Zip (Join-Path $dir "app$i.msixupload") $inUpload | Out-Null }
        return $dir
    }

    function script:Both([string] $Version = "1.2.3.0") { @{ x64 = (New-Manifest x64 -Version $Version); arm64 = (New-Manifest arm64 -Version $Version) } }
}

Describe "check-msix.ps1" {
    BeforeEach {
        $script:Work = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $script:Work | Out-Null
    }

    AfterEach {
        Remove-Item -Recurse -Force $script:Work
    }

    It "x64 と arm64 のパッケージが正しければ成功する" {
        $dir = New-Upload (Both)
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" *> $null } | Should -Not -Throw
    }

    It ".msixupload が 1 つでなければ失敗する" {
        $dir = New-Upload (Both) -Uploads 2
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" } | Should -Throw "*.msixupload が 1 つではありません（2 個）*"
        { & $script:Check -PackageDir $script:Work -ExpectedVersion "1.2.3.0" } | Should -Throw "*（2 個）*"
        $empty = Join-Path $script:Work "empty"; New-Item -ItemType Directory $empty | Out-Null
        { & $script:Check -PackageDir $empty -ExpectedVersion "1.2.3.0" } | Should -Throw "*（0 個）*"
    }

    It ".msixbundle が 1 つでなければ失敗する" {
        $dir = New-Upload (Both) -Bundles 2
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" } | Should -Throw "*.msixbundle が 1 つではありません（2 個）*"
    }

    It "版が違えば失敗する" {
        $dir = New-Upload (Both -Version "1.2.3.4")
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" } | Should -Throw "*版が 1.2.3.4 です（期待: 1.2.3.0）*"
    }

    It "対象 OS が古い、または Desktop でなければ失敗する" {
        $dir = New-Upload @{ x64 = (New-Manifest x64 -MinVersion "10.0.17134.0"); arm64 = (New-Manifest arm64) }
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" } | Should -Throw "*対象 OS が Windows.Desktop 10.0.17134.0 です*"
        Remove-Item -Recurse -Force (Join-Path $script:Work "*")
        $dir = New-Upload @{ x64 = (New-Manifest x64 -Family "Windows.Universal"); arm64 = (New-Manifest arm64) }
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" } | Should -Throw "*対象 OS が Windows.Universal*"
    }

    It "機能が違えば失敗する" {
        $dir = New-Upload @{ x64 = (New-Manifest x64 -Capabilities @("internetClient", "runFullTrust", "broadFileSystemAccess")); arm64 = (New-Manifest arm64) }
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" } | Should -Throw "*機能が broadFileSystemAccess, internetClient, runFullTrust です*"
    }

    It "x64 と arm64 がそろっていなければ失敗する" {
        $dir = New-Upload @{ x64 = (New-Manifest x64) }
        { & $script:Check -PackageDir $dir -ExpectedVersion "1.2.3.0" } | Should -Throw "*アーキテクチャが x64 です（期待: arm64, x64）*"
    }
}
