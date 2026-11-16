# Findings & Decisions

## Requirements
- 动态时间轴必须支持播放头、缩放、平移、任意时间 seek、区间选择和渲染裁剪。
- UGUI 时间轴独立于现有 IMGUI 设置页，不修改原始 ReplayData。
- 歌曲时间是主时间，渲染帧号由区间和 fps 派生。

## Research Findings
- StutterFix 的可复用模式是 Canvas + pooled slots + MaskableGraphic 批量绘制 + 输入拦截。
- 当前仓库已有遗留 Settings.TimelineOn，但此前没有时间轴实现、seek 或区间渲染接口。
- ReplayPlayer 此前只支持按砖块 FastForward；HitSnapshot 没有精确歌曲时间。

## Implemented Findings
- 新增 ReplayTimelineIndex、TimelineSelection、ReplayTimelineState 和 TimelineController。
- 新增 ReplayPlayer.SeekToTime、TryGetSongPosition 与楼层/按键游标重建。
- 新增 TimelineOverlay/TimelineMarkersGraphic UGUI 时间轴，支持播放头、区间手柄、滚轮缩放和平移。
- 新增 VideoRender.RenderRange，并将区间传入 VideoRenderJob，按区间起点选砖、按区间终点停止。
- 拖动时间轴时通过 TimelineController.InputCaptured 隔离玩法输入查询。
- 新增 ReplayTimelineCheck、7 条时间轴形状断言与 MUT-TL1~MUT-TL7 变异验证。

## Verification
- tools/verify.ps1 -Ci -SkipDeploy：全部通过。
- 主项目 Release：0 错误，0 警告。
- 离线套件：274 条断言，0 失败（2026-11-16 本机实跑；提交前工作区有未提交改动，「工作区干净」一项会红，提交后需再跑一次）。
- 只读审计：6 项全部通过。
- MUT-TL1 至 MUT-TL7：全部被检出。

## UGUI Settings Migration（已撤销）
- 结论：UGUI 设置窗口原型（`Pages.Ugui.cs` / `SettingsWindowUgui.cs`）可以编译，
  但会把五页业务状态、原生对话框、云确认和渲染取消等逻辑全部重新接线，回归面过大。
- 现行方案：UMM/IMGUI 宿主保留，`Ui/ModernSettingsWindow.cs` 提供独立 IMGUI 窗口；
  时间轴仍是独立的 UGUI 覆盖层。
- 已删除原型文件与 `build-errors.log` / `build-current.log` / `verify-current*.log` 等过期日志。
- `Pages` 仍是 `internal static partial class`，为后续按页拆分预留。

## Hardening（2026-11-16）
已修：
- `Main.OnToggle(true)` 的 catch 现在调用 `Teardown()`；关闭路径统一走 `Teardown()`，
  卸载补丁/线程、停止回放/录制/渲染，并还原去特效谱面。
- `ConfigManager.SaveConfigs` 与 `MigrateLegacyConfig` 改走 `AtomicFile`（tmp + `File.Replace` + `.bak`）。
- `CloudReplay` 本地恢复改走 `AtomicFile.WriteAllBytes`。
- `Settings.check` 对热键做 `Enum.IsDefined`，非法值回退默认并告警。
- `verify.ps1` 警告判红、检查本仓库工作区、BOM 覆盖 `tools/i18n`、移除 `-SkipBuild`、支持 `SPECTRE_UPSTREAM`。
- `Directory.Build.props` 统一 `ADOFAI_LIBS` / 同级 `adofai-libs`。
- CI 删除无效的 Spectre 克隆，ffmpeg 固定版本 + SHA256，增加版本一致性检查。
- `EffectRemover` 在 Decode 前保存原始 JSON，关闭模组/功能时还原。
- `ReplayCompression` 的 64 MB 诊断文本/注释已修正。

仍待实机确认：
- `SaveLevelAs` 与 `scnEditor.GetExportLevelFiles` 导出路径在去特效开启时仍可能写出去特效版；
  当前靠 `LevelDataStripped` + 关闭还原降低风险，完整覆盖需要实机验证。
- `EffectRemover` 还原依赖 `GDMiniJSON.Json.Deserialize(LevelData.Encode())`，需在真实编辑器里跑一次
  「开启 → 进关 → 关闭 → 保存 → 重进」确认瞬时状态无副作用。

## Resources
- D:/Projects/SpectreReplay/AGENT.md
- D:/Projects/SpectreReplay/docs/verify.md
- D:/Program Files (x86)/Steam/steamapps/common/A Dance of Fire and Ice/UMMMods/StutterFix