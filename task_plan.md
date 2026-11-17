# Task Plan: SpectreReplay 加固与文档对齐

## Goal

在不改变玩家可见行为的前提下，修复审查发现的文档漂移、生命周期回滚、原子写入、
配置校验、门禁与 CI 缺口，并为新增行为补齐离线断言与变异。

## Next Step

完成 P0 文档/仓库卫生后，依次处理原子写入、生命周期回滚、配置校验和门禁加固。

## Current Phase

Phase 6（加固）

## Phases

### Phase 1: 规格与现状
- [x] 从上一轮设计推导时间轴、区间、seek 和渲染需求
- [x] 核对当前工作区、游标模型、渲染入口和遗留 TimelineOn
- **Status:** complete

### Phase 2: 纯逻辑核心
- [x] 实现 ReplayTimelineIndex / TimelineSelection / TimelineState
- [x] 实现 ReplayPlayer.SeekToTime 和按键状态重建
- [x] 为时间查询、区间约束和 seek 行为添加离线断言
- **Status:** complete

### Phase 3: UGUI 时间轴
- [x] 实现 Canvas 宿主、批量 Graphic、播放头与区间手柄
- [x] 实现拖拽、缩放、平移和输入隔离
- [x] 接入已装载回放的显示与卸载清理
- **Status:** complete

### Phase 4: 区间渲染
- [x] 将 RangeStart/RangeEnd 传入 VideoRenderJob
- [x] 对齐播放起点、捕获终点、尾巴和硬停止
- [x] 渲染收尾保持现有取消/失败语义
- **Status:** complete

### Phase 5: 验证与交付
- [x] 运行离线套件、主项目构建和只读审计
- [x] 对新增断言执行变异验证
- [x] 审查 Git diff 并交付结果
- **Status:** complete

### Phase 6: 全设置界面 UGUI 迁移（已撤销）
- [x] 盘点五页控件、动作、配置提交和动态状态
- [x] 实现 UGUI 原型（`Pages.Ugui.cs` / `SettingsWindowUgui.cs`）
- [x] 实测后撤销：保留 UMM/IMGUI 宿主，改用独立 IMGUI 窗口
- [x] 清理原型文件与过期构建日志
- **Status:** superseded

### Phase 7: 加固（已完成）
- [x] 文档与代码对齐（架构、计数、已知问题）
- [x] 配置/云恢复原子写入
- [x] 启用失败回滚与关闭时 UI 状态恢复
- [x] 配置热键校验
- [x] 门禁加固（警告判红、工作区干净、i18n BOM、移除死开关）
- [x] 构建解耦（`ADOFAI_LIBS` / `Directory.Build.props`）
- [x] CI 加固（删除无效克隆、版本一致性、ffmpeg 固定版本）
- [x] 去特效原始谱面快照/恢复
- [x] 为加固行为补断言与变异
- **Status:** complete

### Phase 8: 视频编码第一批（已完成）
- [x] H.264/H.265 选择与 libx265 / hevc_nvenc
- [x] ABR / CRF / CQP / CBR 码率模式
- [x] 软件编码 preset（ultrafast..placebo）
- [x] 14 语言文案与设置持久化
- [x] EncoderMatrixCheck + MUT-ENC1/2/3
- **Status:** complete

### Phase 9: 后续（进行中）
- [x] 2A AV1 + 通用硬件后端（Auto/NVENC/QSV/AMF/CPU）
  - [x] 2A-1 设置模型：`RenderEncoderMode` 瘦身 + `RenderHardware`（0 Auto/1 NVENC/2 QSV/3 AMF/4 CPU）
    - [x] `RenderEncoderMode` 只留 `0=Auto / 1=Fastest / 2=Quality`；后端只由 `RenderHardware` 决定（唯一判定源）
    - [x] 删 `VideoEncoder.cs:18` 的 `Quality.CpuOnly`，并把 `:491` 的 `quality != Quality.CpuOnly` 换成纯 hardware 判定
    - [x] `ConfigManager` 迁移先于 clamp：`3 → RenderHardware=Cpu + 2`；仅在缺 `render_hardware` 键时迁移
    - [x] 同步 `Pages` 下拉/clamp/`DescribeEncoder`、`VideoRenderJob` 两处构造、`EncoderMatrixCheck`
    - [x] `render.codec.cpu` 暂保留，2A-4 硬件下拉复用
  - [x] 2A-2 `EncoderCatalog` + `ResolveCodec` + preflight
    - [x] 启动时跑一次 `-encoders` 缓存；`Auto` 顺序 NVENC→QSV→AMF→软件，按 codec 分别判断
    - [x] 选中具体编码器后 1 帧 preflight，失败回退下一个后端并写日志/UI
    - [x] `WarmUp` 只解析目录 + 预热当前选择，不做 9 个编码器逐个 15 秒探测
    - [x] QSV/AMF 基础参数：QSV 支持 `-global_quality`；AMF 先保证 ABR/CBR
  - [x] 2A-3 AV1 参数与码率映射（软件优先 `libsvtav1`、退回 `libaom-av1`；`av1_nvenc`/`av1_qsv`/`av1_amf`；CQP/CBR 按后端能力回退）
  - [x] 2A-4 UI + 14 语言 + `.tip` + 实际编码器与回退原因显示
  - [x] 2A-5 后端中途失败回退 CPU 重跑
    - [x] `VideoEncoder.IsBackendFailure()` 区分编码器不可用与其他失败
    - [x] 特征表只认强特征，排除 `Invalid argument` / `not supported` 等 stderr 噪声
    - [x] 重跑钩子挂在 `RaiseRenderFinished` 之后、`Destroy` 之前，Job 已清空
    - [x] 只重试一次；UI 显示整段重来原因；重跑期间仍可取消；不重复上报
  - [x] 2A-6 CI ffmpeg 能力断言 + 变异 + 文档
    - [x] `tools/ffmpeg-capability.ps1` 校验期望集合；失败时把 `-encoders` 输出存 artifact
    - [x] 断言只管**发布包里那份 ffmpeg**；玩家自备的仍靠运行时 `EncoderCatalog`
    - [x] `MUT-CI22` 钉住 `libx265` 不得从期望集合漏掉
- [x] 2B-0 颜色先导实验（已实机跑；结论：Gamma 空间 + 确定性超白）
  - [x] `HdrProbe`：`--spectre-hdr-probe` + ARGBHalf/RGBAHalf + `hdr-probe.txt/.png`
  - [x] 只等 `scrConductor.song.isPlaying`，避免选曲界面暗帧
  - [x] 检查 `rt.Create()`；多帧采样 2.5s，记录 `max`/`max_frame`/`nonblack`
  - [x] `tools/hdr-probe.ps1` 解析 `status`/`nonblack`，失败 exit 1
  - [x] 实机数据：`max=2.654297`、`colorspace=0 (Gamma)`、`over1≈0.32–0.34%`、两次采样一致
  - [x] 判定：超白真实存在；纯 10-bit 保不住；HDR10 必须做 gamma→linear + BT.709→BT.2020 转换
- [x] 2B-1 10-bit SDR（代码完成 + 实机 banding 对拍证实）
  - [x] `RenderTextureFormat.ARGB64` + `GraphicsFormat.R16G16B16A16_UNorm` + `AsyncGPUReadback` GraphicsFormat 重载
  - [x] 输出 `yuv420p10le` + BT.709，不加 HDR 元数据；4K 队列按 8 字节/像素限容
  - [x] `tools/ffmpeg-probe.ps1` 增加 10-bit 用例（libx264 / libx265 + yuv420p10le）
  - [x] WholeScreen + 10-bit 显示收益有限提示（8-bit 后备缓冲）
  - [x] 10-bit 时跳过 h264_qsv（无 10-bit 输入），回退 AMF/软件
  - [x] MaxInFlight 先按像素、再对 10-bit 减一档；4K 10-bit 单帧 ~66MB
  - [x] 实机同链双帧 ARGB32 vs ARGB64 回读对比：`ab_uniq8=255 / ab_uniq10=62992`、`ab_steps8=18% / ab_steps10=37%`，10-bit 相邻差非零比翻倍 → banding 改善证实
- [x] 2B-1 10-bit SDR（代码 + 实机对拍全部完成）
- [x] 2B-2 HDR10 + 转换链（首段实机通过）
  - [x] 采集改 `ARGBHalf` / `R16G16B16A16_SFloat`，不能用 UNorm 测/存 >1.0
  - [x] C# 回读 half→float32 平面 `gbrpf32le`（G/B/R 三平面），不能直接喂 half RGB
  - [x] gamma→linear EOTF 解码 + BT.709→BT.2020 色域转换（ffmpeg `zscale`）
  - [x] 输出 `yuv420p10le` + BT.2020 + PQ/SMPTE2084；仅 H.265 + CleanCamera
  - [x] `tools/ffmpeg-probe.ps1` 增加 `gbrpf32le → zscale → libx265 HDR10` 用例
  - [x] 禁止把 Gamma 0–1 直接标 `smpte2084`；PNG 仅作构图参照，测量以 `hdr-probe.txt` 为准
  - [x] 实机 ffprobe：`hevc (Main 10), yuv420p10le(tv, bt2020nc/bt2020/smpte2084)`，三项全过
  - [x] WholeScreen 跳过 HDR10 时打 `Debug.LogWarning`
  - [x] 吞吐实测：SDR 63fps / HDR10 6.2fps；2C 硬件 hevc_nvenc Main10 是优化方向
- [ ] 2C QSV/AMF 全矩阵（需 Intel/AMD 实机；无实机只做形状断言，不宣称已验证）
- [ ] 2D 组件类型过滤（不手填 GameObject 名）
  - [ ] `RenderOverlayExcludeTypes` 字符串数组：`ConfigManager` 目前只有 int/bool/string/float 四类读写路径，需新增数组持久化 + 空值/分隔符归一化（预留 0.25–0.5 天）
  - [ ] UI 显示「已匹配 N 个对象」；配置错误不能表现为「什么都没显示」
- [ ] Pages 拆分与静态状态收敛
- **Status:** in_progress

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| 时间轴状态与 ReplayData 分离 | 选择区间是查看/渲染会话状态，不应写回原始回放 |
| 歌曲时间是唯一主时间 | UI、回放游标和渲染帧通过显式换算连接 |
| UGUI 设置窗口迁移撤销 | 原型能编译但维护成本/回归面大于收益；保留 `ModernSettingsWindow` 独立 IMGUI 窗口 |
| 先实现纯逻辑再接 UI | 让 seek 和区间约束可以离线验证 |
| 用户数据写入必须原子 | 崩溃/断电不能把 `Configs.json` 或云端回补充完整文件截断 |
| 产品改动必须配断言与变异 | 没有会红的断言等于没有验证 |

## Errors Encountered
| Error | Resolution |
|-------|------------|
| apply_patch 调用格式反复失败 | 改用原生 apply_patch 修改计划与产品文件 |
| 新增断言首轮未覆盖两条接线 | 补充对应形状判据并重跑 MUT-TL1/T MUT-TL4 |
| PowerShell 写回 mutate.ps1 丢失 BOM | 用 UTF-8 BOM 字节写回并统一 LF |
| UGUI 迁移原型与真实宿主不一致 | 撤销迁移，删除 `Pages.Ugui.cs` / `SettingsWindowUgui.cs` 及过期日志 |