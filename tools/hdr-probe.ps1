<#
  2B-0 颜色先导实验启动器。
  用 --spectre-hdr-probe 启动游戏；进入关卡后探针会写 hdr-probe.txt / hdr-probe.png。
  脚本只负责启动与等待，不会杀游戏进程。
#>
[CmdletBinding()]
param(
    [string]$GameRoot,
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_gameroot.ps1')

$game = if ([string]::IsNullOrWhiteSpace($GameRoot)) { Get-SrmGameRoot } else { $GameRoot }
$exe = Join-Path $game 'A Dance of Fire and Ice.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "游戏 exe 不存在：$exe"
}

$low = Join-Path $env:USERPROFILE 'AppData\LocalLow'
$start = Get-Date
$proc = Start-Process -FilePath $exe -ArgumentList '--spectre-hdr-probe' -PassThru
Write-Output "已启动：$exe (pid=$($proc.Id))"
Write-Output "进入关卡并等待探针写报告，最多 $TimeoutSec 秒…"

$deadline = $start.AddSeconds($TimeoutSec)
do {
    Start-Sleep -Seconds 2
    $report = Get-ChildItem -LiteralPath $low -Recurse -Filter 'hdr-probe.txt' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $start } |
        Select-Object -First 1
    if ($report) {
        Write-Output "报告：$($report.FullName)"
        $content = Get-Content -LiteralPath $report.FullName -Raw
        $status = if ($content -match '(?m)^status=(.*)$') { $Matches[1].Trim() } else { '' }
        $nonblack = if ($content -match '(?m)^nonblack=(\d+)$') { [int]$Matches[1] } else { -1 }
        Write-Output $content
        $png = Join-Path $report.DirectoryName 'hdr-probe.png'
        if (Test-Path -LiteralPath $png) { Write-Output "PNG：$png" }
        if ($status -ne 'ok' -or $nonblack -le 0) {
            Write-Error "探针未产出有效帧：status=$status nonblack=$nonblack"
            exit 1
        }
        exit 0
    }
} while ((Get-Date) -lt $deadline)

Write-Error "等待超时：没有找到新的 hdr-probe.txt。确认已进入关卡且模组已启用。"
exit 2