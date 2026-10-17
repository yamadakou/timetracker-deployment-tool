<#
  .github/scripts/detect-code-changes.ps1 のテスト（Pester 5 以上）。
  一時ディレクトリに Git のリポジトリを作り、実際のコミットの差分で、GITHUB_OUTPUT に書く code の値を確かめる。

  実行: Invoke-Pester ./tests/scripts
#>

BeforeAll {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:Detect = Join-Path $repo ".github/scripts/detect-code-changes.ps1"

    function script:Invoke-Git {
        $output = & git @args 2>&1
        if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') が失敗しました: $output" }
        return ($output -join "`n")
    }

    # ファイルを書いてコミットし、コミットの SHA を返す（$Files: パス → 内容。内容が $null なら削除）
    function script:New-Commit([hashtable] $Files) {
        foreach ($path in $Files.Keys) {
            if ($null -eq $Files[$path]) {
                Invoke-Git rm -q $path | Out-Null
            } else {
                $full = Join-Path (Get-Location) $path
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $full) | Out-Null
                Set-Content -Path $full -Value $Files[$path]
                Invoke-Git add $path | Out-Null
            }
        }
        Invoke-Git commit -q -m "test" | Out-Null
        return Invoke-Git rev-parse HEAD
    }

    # スクリプトを実行し、GITHUB_OUTPUT に書いた code の値（"true" / "false"）を返す
    function script:Invoke-Detect([hashtable] $Params) {
        $out = Join-Path $script:Work "github_output"
        Remove-Item $out -ErrorAction SilentlyContinue
        & $script:Detect @Params -OutputPath $out *> $null
        return ((Get-Content $out) -replace "^code=", "")
    }
}

Describe "detect-code-changes.ps1" {
    BeforeEach {
        $script:Work = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
        New-Item -ItemType Directory -Path $script:Work | Out-Null
        Push-Location $script:Work
        Invoke-Git init -q -b main | Out-Null
        Invoke-Git config user.email "test@example.com" | Out-Null
        Invoke-Git config user.name "test" | Out-Null
        $script:Root = New-Commit @{ "README.md" = "readme"; "src/App.cs" = "class App {}"; "docs/a.md" = "a" }
    }

    AfterEach {
        Pop-Location
        Remove-Item -Recurse -Force $script:Work
    }

    Context "PR（pull_request）" {
        It "docs/ の下と *.md だけの変更なら false（省く）" {
            $head = New-Commit @{ "docs/a.md" = "changed"; "docs/design/b.png" = "png"; "README.md" = "changed"; "src/Lib/README.md" = "new" }

            Invoke-Detect @{ EventName = "pull_request"; Base = $script:Root; Head = $head } | Should -Be "false"
        }

        It "ドキュメント以外を 1 つでも含むなら true（すべて実行する）" {
            $head = New-Commit @{ "docs/a.md" = "changed"; "src/App.cs" = "class App { }" }

            Invoke-Detect @{ EventName = "pull_request"; Base = $script:Root; Head = $head } | Should -Be "true"
        }

        It "名前に docs を含んでも docs/ の下でなければ true（.github/ や docs で始まる別のフォルダー）" {
            $head = New-Commit @{ "docsite/index.html" = "x"; ".github/workflows/ci.yml" = "on: push" }

            Invoke-Detect @{ EventName = "pull_request"; Base = $script:Root; Head = $head } | Should -Be "true"
        }

        It "src/ の下のファイルを docs/ に移したら true（移す前のファイルも数える）" {
            Invoke-Git mv src/App.cs docs/App.md | Out-Null
            Invoke-Git commit -q -m "move" | Out-Null
            $head = Invoke-Git rev-parse HEAD

            Invoke-Detect @{ EventName = "pull_request"; Base = $script:Root; Head = $head } | Should -Be "true"
        }

        It "ファイルの削除も変更として数える（src/ の下の削除は true）" {
            $head = New-Commit @{ "src/App.cs" = $null }

            Invoke-Detect @{ EventName = "pull_request"; Base = $script:Root; Head = $head } | Should -Be "true"
        }

        It "base が先に進んでいても、分岐点からの PR の変更だけで判定する" {
            $head = New-Commit @{ "docs/a.md" = "pr" }
            Invoke-Git checkout -q -b base $script:Root | Out-Null
            $base = New-Commit @{ "src/App.cs" = "class App { int x; }" }

            Invoke-Detect @{ EventName = "pull_request"; Base = $base; Head = $head } | Should -Be "false"
        }

        It "変更したファイルが 1 つも無ければ true" {
            Invoke-Git commit -q --allow-empty -m "empty" | Out-Null
            $head = Invoke-Git rev-parse HEAD

            Invoke-Detect @{ EventName = "pull_request"; Base = $script:Root; Head = $head } | Should -Be "true"
        }

        It "日本語のファイル名も、ドキュメントとして判定する" {
            $head = New-Commit @{ "docs/手順.md" = "x" }

            Invoke-Detect @{ EventName = "pull_request"; Base = $script:Root; Head = $head } | Should -Be "false"
        }
    }

    Context "main への push" {
        It "ドキュメントだけの push なら false" {
            $head = New-Commit @{ "docs/a.md" = "changed" }

            Invoke-Detect @{ EventName = "push"; Base = $script:Root; Head = $head; Forced = "false" } | Should -Be "false"
        }

        It "コードを含む push なら true" {
            $head = New-Commit @{ "src/App.cs" = "class App { }" }

            Invoke-Detect @{ EventName = "push"; Base = $script:Root; Head = $head; Forced = "false" } | Should -Be "true"
        }

        It "強制 push なら、ドキュメントだけでも true" {
            $head = New-Commit @{ "docs/a.md" = "changed" }

            Invoke-Detect @{ EventName = "push"; Base = $script:Root; Head = $head; Forced = "true" } | Should -Be "true"
        }

        It "ブランチの作成（before が 0 だけ）なら true" {
            $head = New-Commit @{ "docs/a.md" = "changed" }

            Invoke-Detect @{ EventName = "push"; Base = "0000000000000000000000000000000000000000"; Head = $head } | Should -Be "true"
        }

        It "before が空なら true" {
            $head = New-Commit @{ "docs/a.md" = "changed" }

            Invoke-Detect @{ EventName = "push"; Base = ""; Head = $head } | Should -Be "true"
        }

        It "before のコミットが無い（取得できない）なら true" {
            $head = New-Commit @{ "docs/a.md" = "changed" }

            Invoke-Detect @{ EventName = "push"; Base = "1111111111111111111111111111111111111111"; Head = $head } | Should -Be "true"
        }
    }

    Context "そのほかのイベント" {
        It "手動実行（workflow_dispatch）なら、ドキュメントだけでも true" {
            $head = New-Commit @{ "docs/a.md" = "changed" }

            Invoke-Detect @{ EventName = "workflow_dispatch"; Base = $script:Root; Head = $head } | Should -Be "true"
        }
    }

    Context "出力先" {
        It "OutputPath が空なら、ファイルには書かずに終わる" {
            $head = New-Commit @{ "docs/a.md" = "changed" }

            { & $script:Detect -EventName pull_request -Base $script:Root -Head $head -OutputPath "" *> $null } | Should -Not -Throw
            Join-Path $script:Work "github_output" | Should -Not -Exist
        }
    }
}
