<#
  CI 能力断言：只验证**发布包里那份 ffmpeg**。
  玩家游戏目录自备/旧的 ffmpeg 仍由运行时 EncoderCatalog 处理，两者不可互相替代。

  退出码 0 = 期望的编码器都在；1 = 缺编码器（会打印缺失项）。
  无论成败都会把 -encoders 输出写到 -OutFile，CI 失败时作为 artifact 上传。
#>
[CmdletBinding()]
param(
    [string]$Ffmpeg,
    [string]$OutFile = (Join-Path (Get-Location) 'ffmpeg-encoders.txt')
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Ffmpeg)) {
    . (Join-Path $PSScriptRoot '_gameroot.ps1')
    $Ffmpeg = Get-SrmFfmpeg
    if (-not $Ffmpeg) {
        throw "ffmpeg not found. 传入 -Ffmpeg 或先安装游戏。"
    }
}
if (-not (Test-Path -LiteralPath $Ffmpeg)) {
    throw "ffmpeg not found: $Ffmpeg"
}

$output = & $Ffmpeg -hide_banner -encoders 2>&1
$names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($raw in $output) {
    $line = ([string]$raw).Trim()
    if ($line.Length -eq 0) { continue }
    $parts = $line -split '[ \t]+' | Where-Object { $_ -ne '' }
    if ($parts.Count -ge 2 -and $parts[0].Length -ge 6 -and ($parts[0][0] -eq 'V' -or $parts[0][0] -eq 'A') -and $parts[0][2] -eq '.') {
        [void]$names.Add($parts[1])
    }
}

$names | Sort-Object | Set-Content -LiteralPath $OutFile -Encoding utf8

$required = @(
    'libx264', 'libx265', 'libvpx-vp9', 'aac', 'libopus',
    'h264_nvenc', 'hevc_nvenc',
    'h264_qsv', 'hevc_qsv',
    'h264_amf', 'hevc_amf'
)
$av1 = @('libsvtav1', 'libaom-av1', 'av1_nvenc', 'av1_qsv', 'av1_amf')

$missing = @()
foreach ($name in $required) {
    if (-not $names.Contains($name)) { $missing += $name }
}
if (-not ($av1 | Where-Object { $names.Contains($_) })) {
    $missing += 'AV1 (' + ($av1 -join '/') + ')'
}

if ($missing.Count -gt 0) {
    Write-Error ("ffmpeg 能力断言失败，缺少：" + ($missing -join ', '))
    Write-Output ("  编码器清单：$OutFile（已写入，CI 会作为 artifact 上传）")
    exit 1
}

Write-Output ("  ffmpeg 能力断言通过：$($names.Count) 个编码器；$OutFile")