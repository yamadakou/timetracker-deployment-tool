<#
  デスクトップアプリの起動時間とメモリを測る（docs/design/desktop/desktop-blazor-hybrid.md の 8・10。Blazor Hybrid の試作と WinUI 3 のアプリを同じ手順で比べる）。
  -Exe（パッケージにしない exe）か -AppUserModelId（導入した MSIX。-ProcessName でプロセスを探す）で起動し、窓が出るまでの時間を測る。
  -WaitSeconds 待ってから、アプリと、その子のプロセス（WebView2 の msedgewebview2.exe など）の Working Set とプライベートなメモリの合計を測り、
  結果を -Output に JSON で書いて、アプリを終了する。CI の windows ランナーの値は目安（実機と違う）
#>
param(
  [Parameter(Mandatory)] [string] $Name,
  [string] $Exe,
  [string] $AppUserModelId,
  [string] $ProcessName,
  [int] $WaitSeconds = 15,
  [Parameter(Mandatory)] [string] $Output
)
$ErrorActionPreference = 'Stop'
$clock = [Diagnostics.Stopwatch]::StartNew()
if ($Exe) {
  $process = Start-Process -FilePath $Exe -PassThru
} else {
  Start-Process "shell:AppsFolder\$AppUserModelId"
  $process = $null
  while (-not $process -and $clock.Elapsed.TotalSeconds -lt 90) {
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    Start-Sleep -Milliseconds 100
  }
  if (-not $process) { throw "プロセス $ProcessName が起動しません" }
}
while ($process.MainWindowHandle -eq 0 -and $clock.Elapsed.TotalSeconds -lt 90) {
  Start-Sleep -Milliseconds 100
  $process.Refresh()
}
if ($process.MainWindowHandle -eq 0) { throw "$Name の窓が出ません" }
$windowMs = $clock.ElapsedMilliseconds
Start-Sleep -Seconds $WaitSeconds

$all = Get-CimInstance Win32_Process
function Get-Tree([int] $id) {
  $all | Where-Object ProcessId -eq $id
  $all | Where-Object ParentProcessId -eq $id | ForEach-Object { Get-Tree $_.ProcessId }
}
$tree = @(Get-Tree $process.Id)
$live = @($tree | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
$result = [ordered]@{
  name = $Name
  windowMs = $windowMs
  processes = $live.Count
  processNames = @($live | Group-Object ProcessName | ForEach-Object { "$($_.Name) x$($_.Count)" })
  workingSetMB = [math]::Round((($live | Measure-Object WorkingSet64 -Sum).Sum) / 1MB, 1)
  privateMB = [math]::Round((($live | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB, 1)
}
$result | ConvertTo-Json | Set-Content -Path $Output -Encoding utf8
$result | ConvertTo-Json | Write-Host
$live | Sort-Object Id -Descending | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
