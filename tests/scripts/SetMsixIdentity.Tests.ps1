<#
  .github/scripts/set-msix-identity.ps1 のテスト（Pester 5 以上）。
  リポジトリのマニフェスト（Package.appxmanifest）の複製を書き換えて確かめる。

  実行: Invoke-Pester ./tests/scripts
#>

BeforeAll {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:SetIdentity = Join-Path $repo ".github/scripts/set-msix-identity.ps1"
    $script:Original = Join-Path $repo "src/Timetracker.Controller.Desktop/Package.appxmanifest"

    function script:Read-Manifest { [xml] (Get-Content $script:Manifest -Raw) }
}

Describe "set-msix-identity.ps1" {
    BeforeEach {
        $script:Manifest = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString() + ".appxmanifest")
        Copy-Item $script:Original $script:Manifest
    }

    AfterEach {
        Remove-Item $script:Manifest
    }

    It "タグの版を X.Y.Z.0 にし、ID が無ければ今の値のままにする" {
        $before = Read-Manifest
        & $script:SetIdentity -Manifest $script:Manifest -Tag "v1.2.3" *> $null
        $after = Read-Manifest
        $after.Package.Identity.Version | Should -Be "1.2.3.0"
        $after.Package.Identity.Name | Should -Be $before.Package.Identity.Name
        $after.Package.Identity.Publisher | Should -Be $before.Package.Identity.Publisher
        $after.Package.Properties.PublisherDisplayName | Should -Be $before.Package.Properties.PublisherDisplayName
        # 機能などほかの記述は変えない
        $after.Package.Capabilities.OuterXml | Should -Be $before.Package.Capabilities.OuterXml
    }

    It "Partner Center の ID と発行元の表示名を設定する" {
        & $script:SetIdentity -Manifest $script:Manifest -Tag "v10.0.1" -Name "12345Contoso.Tool" -Publisher "CN=ABC" -PublisherDisplayName "Contoso" *> $null
        $after = Read-Manifest
        $after.Package.Identity.Name | Should -Be "12345Contoso.Tool"
        $after.Package.Identity.Publisher | Should -Be "CN=ABC"
        $after.Package.Identity.Version | Should -Be "10.0.1.0"
        $after.Package.Properties.PublisherDisplayName | Should -Be "Contoso"
    }

    It "タグが vX.Y.Z の形でなければ失敗し、マニフェストを変えない" {
        $before = Get-Content $script:Manifest -Raw
        { & $script:SetIdentity -Manifest $script:Manifest -Tag "v1.2" } | Should -Throw "*vX.Y.Z の形ではありません*"
        { & $script:SetIdentity -Manifest $script:Manifest -Tag "1.2.3" } | Should -Throw "*vX.Y.Z の形ではありません*"
        Get-Content $script:Manifest -Raw | Should -Be $before
    }

    It "発行元が CN= で始まらなければ失敗し、マニフェストを変えない" {
        $before = Get-Content $script:Manifest -Raw
        { & $script:SetIdentity -Manifest $script:Manifest -Tag "v1.2.3" -Publisher "Contoso" } | Should -Throw "*CN= で始まる値*"
        Get-Content $script:Manifest -Raw | Should -Be $before
    }
}
