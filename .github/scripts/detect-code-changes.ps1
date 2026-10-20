<#
.SYNOPSIS
  CI（.github/workflows/ci.yml）で、7 つのチェックを実行する必要があるか（ドキュメント以外の変更があるか）を判定する。

.DESCRIPTION
  変更したファイルがドキュメント（docs/ の下と *.md）だけなら code=false、それ以外を 1 つでも含むなら code=true を
  GITHUB_OUTPUT に書く。ドキュメントは、ビルド・テスト・コンテナイメージ・スクリプトのテストのどれにも使われていない。
  次の場合は、確かめ漏れを避けるため code=true にする（すべて実行する）。
  - 手動実行（workflow_dispatch）など、pull_request と push 以外のイベント
  - 比べる相手が無い（ブランチの作成で before が 0 だけ、強制 push、比べる相手のコミットが無い）
  - 変更したファイルが 1 つも無い

.EXAMPLE
  ./.github/scripts/detect-code-changes.ps1 -EventName pull_request -Base <base の SHA> -Head <head の SHA>
#>
#Requires -Version 7
param(
    [Parameter(Mandatory)] [string] $EventName,
    [string] $Base,
    [string] $Head,
    [string] $Forced,
    [string] $OutputPath = $env:GITHUB_OUTPUT
)
$ErrorActionPreference = "Stop"

# ドキュメントだけか（docs/ の下と *.md）。空のときは false（すべて実行する）
function Test-DocsOnly([string[]] $Files) {
    if ($Files.Count -eq 0) { return $false }
    foreach ($file in $Files) {
        if (-not ($file.StartsWith("docs/") -or $file.EndsWith(".md"))) { return $false }
    }
    return $true
}

function Get-ChangedFiles {
    if ($EventName -notin @("pull_request", "push")) {
        return @{ Reason = "$EventName のため、すべて実行します。" }
    }
    if ($Forced -eq "true") {
        return @{ Reason = "強制 push のため、すべて実行します。" }
    }
    if ([string]::IsNullOrEmpty($Base) -or $Base -match "^0+$") {
        return @{ Reason = "比べる相手のコミットが無いため、すべて実行します。" }
    }
    # 三点リーダー: base と head の分岐点から head までの変更（PR の差分と同じ）。
    # --no-renames: 名前の変更を削除と追加に分け、移す前のファイル（src/ の下など）も数える
    $output = & git -c core.quotepath=false diff --name-only --no-renames "$Base...$Head" 2>&1
    if ($LASTEXITCODE -ne 0) {
        return @{ Reason = "差分を取れなかったため、すべて実行します（$($output -join ' ')）。" }
    }
    # 2>&1 で受けた標準エラー（警告など）は ErrorRecord になるため、ファイル名（文字列）だけを取り出す
    return @{ Files = @($output | Where-Object { $_ -is [string] -and $_ }) }
}

$changes = Get-ChangedFiles
if ($changes.Reason) {
    $code = $true
    Write-Host $changes.Reason
} else {
    $files = @($changes.Files)
    $code = -not (Test-DocsOnly $files)
    Write-Host "変更したファイル（$($files.Count) 件）:"
    $files | ForEach-Object { Write-Host "  $_" }
    Write-Host ($code ? "ドキュメント以外の変更があるため、すべて実行します。" : "ドキュメントだけの変更のため、7 つのチェックを省きます。")
}

if ($OutputPath) {
    "code=$($code.ToString().ToLowerInvariant())" | Add-Content -Path $OutputPath -Encoding utf8NoBOM
}
