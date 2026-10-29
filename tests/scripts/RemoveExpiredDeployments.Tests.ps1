<#
  .github/scripts/remove-expired-deployments.ps1 のテスト（Pester 5 以上）。
  az resource list の出力の形の JSON を一時ファイルに書き、-PlanOnly で期限切れと判断したデプロイだけを確かめる（Azure には接続しない）。

  実行: Invoke-Pester ./tests/scripts
#>

BeforeAll {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:Remove = Join-Path $repo ".github/scripts/remove-expired-deployments.ps1"
    $script:Now = [DateTimeOffset]::Parse("2026-10-29T10:00:00Z")

    function script:Resource([string] $Group, [hashtable] $Tags) {
        [pscustomobject]@{ name = "r"; resourceGroup = $Group; tags = [pscustomobject]$Tags }
    }

    function script:Plan([object[]] $Resources) {
        $file = Join-Path $TestDrive "resources.json"
        ConvertTo-Json -InputObject @($Resources) -Depth 5 | Set-Content -Path $file
        @(& $script:Remove -SubscriptionId "sub" -ResourcesFile $file -Now $script:Now -PlanOnly)
    }
}

Describe "remove-expired-deployments.ps1" {
    It "期限を過ぎたデプロイだけを、リソースグループとアプリ名ごとに 1 件として選ぶ" {
        $expired = Plan @(
            (Resource "tt-test-1" @{ "managed-by" = "ttdeploytool"; "tt-app-name" = "timetracker"; "tt-expires-at" = "2026-10-20T14:59:59Z" }),
            (Resource "TT-TEST-1" @{ "managed-by" = "ttdeploytool"; "tt-app-name" = "Timetracker"; "tt-expires-at" = "2026-12-01T14:59:59Z" }),
            (Resource "rg-future" @{ "managed-by" = "ttdeploytool"; "tt-app-name" = "demo"; "tt-expires-at" = "2026-11-30T14:59:59Z" }),
            (Resource "rg-none" @{ "managed-by" = "ttdeploytool"; "tt-app-name" = "demo"; "tt-expires-at" = "none" }),
            (Resource "rg-legacy" @{ "managed-by" = "timetracker-deployment-tool"; "tt-app-name" = "old"; "tt-expires-at" = "2026-10-01T00:00:00Z" })
        )

        $expired.Count | Should -Be 2
        $expired[0].ResourceGroup | Should -Be "rg-legacy"
        $expired[1].ResourceGroup | Should -Be "tt-test-1"
        $expired[1].AppName | Should -Be "timetracker"
        $expired[1].ExpiresAt | Should -Be ([DateTimeOffset]::Parse("2026-10-20T14:59:59Z")) -Because "まとめたリソースのうち、最も早い期限"
    }

    It "本ツールのものでない・アプリ名や期限が無い・読めない期限のリソースは選ばない" {
        $expired = Plan @(
            (Resource "rg-other" @{ "managed-by" = "someone"; "tt-app-name" = "x"; "tt-expires-at" = "2026-10-01T00:00:00Z" }),
            (Resource "rg-noname" @{ "managed-by" = "ttdeploytool"; "tt-expires-at" = "2026-10-01T00:00:00Z" }),
            (Resource "rg-noexpiry" @{ "managed-by" = "ttdeploytool"; "tt-app-name" = "x" }),
            (Resource "rg-bad" @{ "managed-by" = "ttdeploytool"; "tt-app-name" = "x"; "tt-expires-at" = "2026/10/01" }),
            [pscustomobject]@{ name = "untagged"; resourceGroup = "rg-untagged"; tags = $null }
        )

        $expired.Count | Should -Be 0
    }
}
