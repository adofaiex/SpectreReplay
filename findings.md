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
- 离线套件：307 条断言，0 失败（2026-11-17 本机实跑；提交前工作区有未提交改动，「工作区干净」一项会红，提交后需再跑一次）。
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

## 2B-0 颜色先导实验（2026-11-17）
- 探针：`--spectre-hdr-probe`，ARGBHalf/RGBAHalf，等待 `scrConductor.song.isPlaying`，采样 2.5s。
- 两次实机结果：
  - `max=2.654297`（两次完全一致，fp16 量化）
  - `max_frame=3/4`
  - `over1=111202/81457`，按像素占比 **0.318% / 0.340%**
  - `frames=38/26`（同步回读拖慢帧率，窗口都是 2.5s）
  - `nonblack=35020800/23952542`
  - `colorspace=0`（Unity `QualitySettings.activeColorSpace` = Gamma）
- 结论：
  - ADOFAI 是 **Gamma 色彩空间**，帧缓冲是显示编码值，不是线性光。
  - 存在确定性超白源（命中闪光/固定 HDR 颜色），HDR 收益真实。
  - 纯 10-bit SDR（BT.709）会把 >1.0 钳掉，**不能保留超白**。
  - 若做 HDR10：必须先 gamma→linear 解码 + BT.709→BT.2020 色域转换，再 PQ 编码。
  - 不能把 Gamma 0–1 直接标 `smpte2084`；PNG 只作构图参照，测量以 `hdr-probe.txt` 为准。
- 2B 走向：**2B-1 10-bit SDR（banding）独立推进；2B-2 HDR10 + 转换链单独立项**。

## 2B-1 banding 对拍（2026-11-17 实机）
- 同一相机链紧邻两帧分别渲到 ARGB32（8-bit）与 ARGB64（10-bit），统计每通道唯一量化值数与亮度相邻差非零比。
- 数据：`ab_uniq8=255 / ab_uniq10=62992`（各自接近 256 / 65536 阶上限）、`ab_steps8=18% / ab_steps10=37%`。
- **结论：10-bit 的相邻差非零比是 8-bit 的约 2 倍**（37% vs 18%）——同一渐变过渡带内 10-bit 走出两倍以上的独立亮度阶，banding 改善证实。
- A/B 对拍由 `--spectre-hdr-probe` 顺带完成（`ab_*` 字段 + `hdr-probe-ab8/10.png`），断言 ④ 与 `MUT-HDR7` 守住。

## 2B-2 HDR10 首段（2026-11-17）
- 采集：`RenderTextureFormat.ARGBHalf` + `GraphicsFormat.R16G16B16A16_SFloat`。
- C#→ffmpeg：`half→float32` + 打包 RGBA→平面 G/B/R，输出 `gbrpf32le`；不能直接喂 half RGB。
- 转换：ffmpeg `zscale=transferin=iec61966-2-1:primariesin=bt709:matrixin=bt709:transfer=smpte2084:primaries=bt2020:matrix=bt2020nc`。
- 编码：`libx265`，`yuv420p10le`，`-colorspace bt2020nc -color_primaries bt2020 -color_trc smpte2084`，x265 `hdr10=1` 元数据。
- 边界：仅 H.265 + CleanCamera；WholeScreen 自动不按 HDR10 处理；光标叠加禁用。
- >1.0 的处理：zscale 的 sRGB 曲线按解析式延拓；不把它写成标准 EOTF，最终 nits 取决于 mastering display。
- 实测：`ffmpeg-probe.ps1` 的 `gbrpf32le → zscale → libx265 HDR10` 全链退出 0；ffprobe 确认 `yuv420p10le (bt2020nc/bt2020/smpte2084)`。
- 断言：`Hdr10Check` 11 条；`MUT-HDR10-1..3` 全部变红。
- 待实机：HDR 显示器/SDR 屏视觉确认；`max-cll` 首版固定 `1800,600`（按 2B-0 峰值估计），实机再校准。

## Resources
- D:/Projects/SpectreReplay/AGENT.md
- D:/Projects/SpectreReplay/docs/verify.md
- D:/Program Files (x86)/Steam/steamapps/common/A Dance of Fire and Ice/UMMMods/StutterFix