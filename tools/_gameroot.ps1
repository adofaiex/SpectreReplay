# 共用：定位游戏根目录 / 运行时模组目录 / ffmpeg。
#
# **为什么要有这个文件。** 四个脚本（deploy.ps1 / verify.ps1 / ffmpeg-probe.ps1 /
# convert-probe.ps1）原先各自硬编码了同一个路径 `<game>\Mods\SpectreReplay`，
# 路径解析因此分散在四处。现在只留这一份，其它脚本点源引用。
#
# **加载器扫描哪个目录，以游戏日志为准。** 2026-11-23 实测，游戏日志
# `%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log`
# 第一条 `[Manager] Mods path:` 指向 `<game>\Mods`，并从那里加载了 13/13 个模组；
# `<game>\UMMMods` 不被加载。2026-10-25 的记录认为加载器扫的是 UMMMods，依据是
# `UnityModManager\Log.txt`。该文件现已不存在，当前日志显示加载的是 Mods，那条结论作废。
#
# ── 解析顺序（先 Mods，找不到退 UMMMods）──────────────────────
#   1. 显式参数 -ModsRoot <path>（最高优先，调试/异机用）
#   2. 环境变量 SRM_MODS_ROOT
#   3. <game>\Mods\<模组>       —— 存在且**看起来是真模组目录**时用（当前加载器读的目录）
#   4. <game>\UMMMods\<模组>    —— 同上；加载器不加载它，只在 Mods 里没有模组时作为回退
#   5. <game>\Mods              —— 都没有时作为**写入默认值**（首次部署要往哪放）
#
# **判据是「目录里真有模组」，不是「目录存在」。** 目录存在但没有模组时，加载器看不见它；
# 只判存在会挑中一个空目录，于是部署显示成功、加载器却看不见。所以判据是
# 「该目录下有至少一个 *\Info.json」，即它真的能被 UMM 扫出模组来。
#
# ── 用法（点源，不执行任何有副作用的事）────────────────────────
#   . (Join-Path $PSScriptRoot '_gameroot.ps1')
#   $mods  = Get-SrmModsRoot        # @{ Root=...; IsValid=$true/$false; ... }
#   $mod   = Get-SrmModDir          # <mods>\SpectreReplay
#   $ff    = Get-SrmFfmpeg          # 与 VideoEncoder.LocateEncoder 同口径
#
# 注意：本文件必须存成 **UTF-8 带 BOM**。deploy.ps1 由 verify.ps1 用
# `powershell`（5.1）调用，5.1 按 ANSI 读没有 BOM 的脚本会把中文读成乱码
# 并直接 ParserError。写完记得补 BOM（见 tools/ 下各脚本开头同一段警告）。

$script:SrmGameRootDefault = 'D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice'
$script:SrmModFolderName = 'SpectreReplay'

function Get-SrmGameRoot {
    # 环境变量优先，便于换 Steam 库位置或 CI；默认值是本机实际安装路径。
    if ($env:ADOFAI_GAME) { return $env:ADOFAI_GAME }
    return $script:SrmGameRootDefault
}

function Test-SrmLooksLikeModsRoot {
    <#
      判据：该目录下至少有一个子目录带 Info.json / info.json。
      UMM 就是按 <mods>\<名字>\Info.json 发现模组的，所以这条判据
      「像不像加载器会扫的目录」与真实行为同口径，而不是靠名字猜。
    #>
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $false }
    try {
        foreach ($d in (Get-ChildItem -LiteralPath $Path -Directory -ErrorAction Stop)) {
            foreach ($n in @('Info.json', 'info.json')) {
                if (Test-Path -LiteralPath (Join-Path $d.FullName $n) -PathType Leaf) { return $true }
            }
        }
    } catch {
        return $false
    }
    return $false
}

function Get-SrmModsRootCandidates {
    <#
      模组父目录的候选，按优先级排列（只列路径，不判断它像不像模组目录）：
        显式参数 → 环境变量 SRM_MODS_ROOT → <game>\Mods → <game>\UMMMods
      Get-SrmModsRoot 从中选出第一个「像模组目录」的；Get-SrmSpectreFfmpeg 在其中找 Spectre。
    #>
    param([string]$ModsRoot)

    $game = Get-SrmGameRoot
    $candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($ModsRoot)) {
        $candidates += @{ Path = $ModsRoot; Source = '显式参数' }
    }
    if ($env:SRM_MODS_ROOT) {
        $candidates += @{ Path = $env:SRM_MODS_ROOT; Source = '环境变量' }
    }
    $candidates += @{ Path = (Join-Path $game 'Mods');    Source = 'Mods' }
    $candidates += @{ Path = (Join-Path $game 'UMMMods'); Source = 'UMMMods' }
    return $candidates
}

function Get-SrmModsRoot {
    <#
      返回一个描述对象，不做任何写入：
        Root      最终选中的模组父目录
        IsValid   Root 是否像真模组目录（$false = 只是写入默认值，加载器其实没有它）
        Source    选中的来源，用于日志：「显式参数 / 环境变量 / Mods / UMMMods / 默认」
    #>
    param([string]$ModsRoot)

    $game = Get-SrmGameRoot
    foreach ($c in (Get-SrmModsRootCandidates -ModsRoot $ModsRoot)) {
        if (Test-SrmLooksLikeModsRoot -Path $c.Path) {
            return [pscustomobject]@{
                Root    = $c.Path
                IsValid = $true
                Source  = $c.Source
            }
        }
    }

    # 一个都不像模组目录：往 Mods 写（首次部署的常规落点）。
    # IsValid=$false 让调用方能把「要部署但加载器还不认这个目录」说清楚，
    # 而不是静默部署到一个没人读的地方——这是 2026-10-25 记录过的一类症状。
    return [pscustomobject]@{
        Root    = (Join-Path $game 'Mods')
        IsValid = $false
        Source  = '默认'
    }
}

function Get-SrmSpectreFfmpeg {
    <#
      Spectre 模组自带的 ffmpeg，只作为 deploy 的备用来源（只读，不改 Spectre）。
      Spectre 不一定和 SpectreReplay 在同一个父目录：本机 Spectre 在 UMMMods\Spectre，
      Mods 里没有它。所以按全部候选父目录依次找，返回第一个存在的 Spectre\ffmpeg.exe；
      找不到返回 $null。
    #>
    param([string]$ModsRoot)

    foreach ($c in (Get-SrmModsRootCandidates -ModsRoot $ModsRoot)) {
        $p = Join-Path $c.Path 'Spectre\ffmpeg.exe'
        if (Test-Path -LiteralPath $p -PathType Leaf) { return $p }
    }
    return $null
}

function Get-SrmModDir {
    # 模组自己的目录：<mods root>\SpectreReplay
    param([string]$ModsRoot)
    $r = Get-SrmModsRoot -ModsRoot $ModsRoot
    return (Join-Path $r.Root $script:SrmModFolderName)
}

function Get-SrmFfmpeg {
    <#
      与 SpectreReplay.Features.VideoRender.VideoEncoder.LocateEncoder 同口径（VideoEncoder.cs 约 :188-206）：
        ① 模组目录\ffmpeg.exe
        ② 游戏根目录\ffmpeg.exe
        ③ 系统 PATH 上的 ffmpeg（游戏侧同样以裸名 "ffmpeg" 交给系统解析，是最后一级）
      游戏侧还会对候选做 -version 能跑的校验（VerifyEncoder）；这里只判「文件在不在」，
      用来回答部署脚本关心的问题：有没有一份游戏会去找的 ffmpeg。
      找不到返回 $null——**不要 throw**，由调用方决定是报错还是提示。
    #>
    param([string]$ModsRoot)

    $game = Get-SrmGameRoot
    $modDir = Get-SrmModDir -ModsRoot $ModsRoot

    $candidates = @(
        (Join-Path $modDir 'ffmpeg.exe'),
        (Join-Path $game 'ffmpeg.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c -PathType Leaf) { return $c }
    }

    # PATH 回退：最后一级，避免 probe 在未部署机器上完全跑不起来
    $onPath = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    return $null
}

function Format-SrmModsDiagnosis {
    # 解析失败时贴出全部候选，别再让人靠猜——本 bug 排查成本全在"到底读哪个目录"。
    param([string]$ModsRoot)

    $game = Get-SrmGameRoot
    $lines = @()
    $lines += "游戏根目录：$game"
    foreach ($name in @('Mods', 'UMMMods')) {
        $p = Join-Path $game $name
        if (-not (Test-Path -LiteralPath $p -PathType Container)) {
            $lines += ("  {0,-8} 不存在" -f $name)
        } elseif (Test-SrmLooksLikeModsRoot -Path $p) {
            $lines += ("  {0,-8} 存在且含模组" -f $name)
        } else {
            $lines += ("  {0,-8} 存在但没有模组（空目录）" -f $name)
        }
    }
    $chosen = Get-SrmModsRoot -ModsRoot $ModsRoot
    $lines += "选定：$($chosen.Root)（来源 $($chosen.Source)）"
    $lines += '加载器实际扫描的目录以游戏日志 Player.log 的 [Manager] Mods path 为准（2026-11-23 实测为 Mods）'
    return ($lines -join [Environment]::NewLine)
}
