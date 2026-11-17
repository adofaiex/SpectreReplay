# Progress Log

## Session: 2026-11-01

### Current Status
- **Phase:** 5 - 交付
- **Started:** 2026-11-01

### Actions Taken
- 反编译 StutterFix，提取 UGUI Canvas、批量 Graphic 和输入拦截模式。
- 实现时间索引、动态区间、任意时间 seek 和 UGUI 时间轴覆盖层。
- 将区间接入 VideoRender/VideoRenderJob，并补充输入隔离。
- 新增离线时间轴检查、形状断言和 MUT-TL1~MUT-TL7 变异。
- 运行完整 verify 闸门并确认全部通过。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| 主项目构建 | 0 错误 0 警告 | 0 错误 0 警告 | pass |
| 离线断言 | 非 0 且 0 失败 | 164 条，0 失败 | pass |
| 只读审计 | 全过 | 全过 | pass |
| 时间轴变异 | 每条都变红 | MUT-TL1~TL7 全部检出 | pass |
| git diff --check | 无问题 | 无问题 | pass |

### Errors
| Error | Resolution |
|-------|------------|
| apply_patch 调用格式反复失败 | 改用 PowerShell 写入文件 |
| 新增断言首轮漏掉两条接线 | 补判据并重跑 MUT-TL1/T MUT-TL4 |
| mutate.ps1 写回丢失 BOM | 用 UTF-8 BOM 字节写回并统一 LF |
# Progress

## 2026-11-02 — Phase 6 start

- Re-read project constraints and planning state.
- Preserved all uncommitted timeline work.
- Confirmed the product code has not yet been migrated.
- Identified the reusable UGUI pieces in `SpectreReplay/Ui/Timeline`.
- Confirmed StutterFix patterns for hit blocking, pooled graphics, and runtime sprite creation.
- Next: implement the settings window skeleton and shared UGUI controls.

## Session: 2026-11-12

### Current Status
- **Task:** 渲染成片音画偏移修复（方案 A：mux 侧 -itsoffset）
- **Result:** 已完成并实机验证对齐

### Actions Taken
- 根因确认：实机画面比音频滞后校准量（inputOffset），离线渲染没有扬声器延迟补偿，校准被烧进成片。
- MuxWith 增加音频偏移参数：正值音频侧 itsoffset、负值视频侧（避开负 PTS）、零值命令行与历史一致；抢救命令同步。
- 新增设置 RenderAudioOffsetMs（默认 -1 跟随 scrConductor.calibration_i，0..500ms 显式覆盖），界面开关+滑条，i18n 4 键 × 14 语。
- 新增 MuxOffsetCheck（20 行断言）+ MUT-MUX1/MUT-MUX2 变异，闸门 188 条全绿（commit 4140e18），文档同步（bdfeb2f）。
- 实机重渲 Starlight Ignition：tick 整体 +0.124s，第一击视觉锚点 2.767s 与音乐首响 2.741s 残差 ≈26ms（修复前 ≈150ms），对齐确认。

## Session: 2026-11-12 (2)

### Current Status
- **Task:** 对外按键接口（KeyViewer 拉取 API + ReplayInput 通用推桥）
- **Result:** 已完成，已部署

### Actions Taken
- SpectreAPI 新增按键查询面（ApiVersion 0.5→0.6）：IsReplayActive / IsReplayKeyHeld / WasReplayKeyPressedThisFrame / GetHeldReplayKeys（never-null）/ ReplayKeyChanged 事件（主线程封送 + 逐订阅者隔离）/ UnityKeyToVk；ManagedInput.Note 翻转即发事件。
- 反编译 RainingKeys 0.5.0：确认 ReplayInput 推式契约（OnStartInputs/OnKeyPressed/OnKeyReleased/OnEndInputs，受控模式停物理轮询）为跨模组通用约定；按用户要求按类型名扫描全部程序集，找到几个驱动几个，不绑某一家。
- ReplayInputBridge 五处接线（Start/Stop/PumpInput 边沿/Rebuild 重播种/Main 复位）；VK→Unity KeyCode 走 SkyHook 逆链 + VK_APPS=93 互逆特判；Reseed 用「重建容器 + 净按住态补 Down」。
- 实测修正三处事实错误：KeyCode.Menu 实为 319（反射实测，旧注释 276/文档 277 均错）；文档版本探测改成员反射（字符串比较在 0.10 会误判）；暂停语义核实（无暂停钩子停回放，IsReplayActive 保持 true）。
- 新增 ApiSurfaceCheck（7 行）+ MUT-API1/2/3、ReplayInputBridgeCheck（7 行，含「不许绑死一家」反向判据）+ MUT-RI1/RI2，全部实测变红。闸门 202 条全绿，已部署。

## Session: 2026-11-13

### Current Status
- **Task:** 特效事件驱动审计（只读）＋ 0.3.1 版本发布
- **Result:** 两件均完成；v0.3.1 已推送 origin（提交 64f2d80，tag v0.3.1）

### Actions Taken
- 特效事件驱动审计（只读扫 `adofai-libs/source/Assembly-CSharp`，不改代码）：游戏本体无 JRP 雨滴级别墙钟隐患。ffxPlusBase 事件派发走 `cond.songposition_minusi`（被钉歌曲时钟）；50+ ffx 子类动画全为 DOTween（吃被钉 dt）；4 处 `ParticleSystem.Simulate` 全部定性无害（编辑器 scrub / 常量预热 / Start 预热 / 菜单）；Stopwatch 0 命中；`scrConductor` 的 DateTime 在输入域（AsyncInputManager.currFrameTick），非特效；unscaledDeltaTime 44 文件按构造安全；videoBG 由模组 PinVideoPlayers 接管。
- 唯一外部时钟族：`GetSpectrumData` 谱线视觉（scrBarMaker / scrBgbarnew / scrVolumeTracker*）。模组 AudioRenderer 离线混音已把音频 DSP 纳入帧驱动权威，理论一致但未实测——建议实机验证带 `TrackColorType.Volume` 或背景条的关卡。
- 附带发现（游戏本体）：静音段 `Mathf.Log(0) = -Infinity`——scrVolumeTrackerFade 无保护；scrFloor Volume 走 Color.Lerp Clamp01 安全；scrVolumeTrackerScale / scrBgbarnew 自带 0 保护。
- 0.3.1 发布：三处版本文件 → Release 构建 0 警告 → verify 闸门 206 条全绿（后台跑，含部署一致性）→ 版本提交 64f2d80（只含版本文件，格式照 35c8e60）→ 附注 tag v0.3.1（SSH 签名）→ 用户确认后 `git push origin master v0.3.1`。
- AGENT.md 新增 §7.8 版本发布配方（版本文件三处、tag 必须 `-a -m`、推送先确认）。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| 主项目构建 | 0 错误 0 警告 | 0 错误 0 警告（3.17s） | pass |
| verify 闸门 | 非 0 断言且 0 失败 | 206 条断言，0 失败，全部通过 | pass |
| 版本提交范围 | 仅三处版本文件 | info.json / repo.json / AssemblyInfo.cs（5 处替换） | pass |
| tag v0.3.1 | 附注+SSH 签名，指版本提交 | 指向 64f2d80 | pass |
| 推送 | origin master + tag 更新 | 8a0b5c8..64f2d80、`* [new tag] v0.3.1` | pass |

### Errors
| Error | Resolution |
|-------|------------|
| 裸 `git tag v0.3.1` 弹 Vim 挂起（先吃满 run_code 120s 墙钟，再 60s 超时） | 全局 `tag.gpgSign=true` 把 tag 升级为附注+SSH 签名；改用 `git tag -a v0.3.1 -m "…"`，教训写入 AGENT.md §7.8 |
| bash 末尾 Remove-Item 被硬安全策略整条拦截，连带 git add/commit 未执行 | 删除类命令不与关键命令混排；临时提交信息文件留在 `.git/` 内不删 |

## Session: 2026-11-14

### Current Status
- **Task:** 清理审计 A-F7 死代码（ReplayKeys 常量类）
- **Result:** 已完成

### Actions Taken
- 按审计 A-F7 整套删除：`Features/Replay/Data/ReplayKeys.cs`（33 个旧字典格式键名常量，全仓 0 引用）+ 12 个文件里的 `using static` / `using K` 死引用（ReplayDecoder / ReplayFileInfo / Patch_Lifecycle / Patch_Playback / Patch_Recording / ReplayAnalyzer / ReplayManager / ReplayRecorder / VideoRender / VideoRenderJob / Pages / SpectreState）。
- UiPreview.csproj 同步清理：移除 ReplayKeys.cs 的 Compile 项与头部清单注释行。
- 审计文档 A-replay-core.md 的 F7 条目标注已修。
- 注意区分：SpectreAPI.GetHeldReplayKeys() 是活的对外按键查询 API，与被删常量类无关，未动。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.6s） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条断言，0 失败，全部通过（含死代码审计 6 项） | pass |
| 残留检查 | SpectreReplay 源码内 ReplayKeys 仅剩 GetHeldReplayKeys API | 仅 SpectreAPI.cs 一处 | pass |

## Session: 2026-11-14 (2)

### Current Status
- **Task:** 修 bug：纯净相机渲染时游戏的判定文本（scrHitTextMesh 飘字）没有被隐藏
- **Result:** 已完成

### Actions Taken
- 根因：游戏判定飘字是世界空间 TextMeshPro（scrHitTextMesh，sortingOrder 100），由主相机直接画进捕获纹理——RenderOverlayCapture 的画布 opt-in 只管 UGUI Canvas，罩不住它；回放命中会按玩家自己的游戏设置（hitMarginText / hideHitMargin）重新喷飘字，纯净成片里全是判定文本。
- 游戏侧全部飘字调用点（scrPlanet 的命中/OverPress、DivineIntervention 与 LaserSpawner 的 FailMiss 广播）汇入 scrHitTextManager.ShowHitText 唯一入口（RDC.noHud / 编辑器自动播放 / CheckHidden 既有门都在其中），前缀在此早退即可。
- 修法（Patch_VideoHideHitText，VideoRenderPatches.cs）：常驻前缀 + 体内早退（Patch_VideoLockAsyncAngle 同款姿态），只在 `VideoRender.Active && CurrentSurface == CleanCamera` 时拦。
- 门面新增 `VideoRender.CurrentSurface`：帧源构造上报、CleanupOnConstructionFailure 与 Dispose 清、ResetLeakedPins 兜底复位。「游戏画面」(WholeScreen) 来源不干预——拍的就是后备缓冲，飘字本来就在屏上。
- 新增 2 条形状判据（前缀门只拦 CleanCamera + CurrentSurface 随帧源装/卸，折叠进 UiLogicShapeCheck 汇总行，181→183 条）+ MUT-U117/U118 实测变红；verify.md 的 UiLogicShapeCheck 行已登记。
- 已知噪声（非本次引入）：MUT-U118 跑时连带 ㊴h 变红——mutate.ps1 的 SetLines 用 WriteAllLines 把目标文件重写成 CRLF，而 ㊴h 用 `\n` 精确串匹配同一文件；MUT-U53/U54 改同一文件时同理。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.0s） | pass |
| MUT-U117 | 前缀门判据变红 | ✓ 被检出（失败 1 条） | pass |
| MUT-U118 | CurrentSurface 接线判据变红 | ✓ 被检出（失败 2 条，含 ㊴h CRLF 连带） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 183 条含新增 2 条），0 失败，全部通过 | pass |

## Session: 2026-11-14 (3)

### Current Status
- **Task:** 新功能：跨版本精准度显示——3.4.0 上显示 ≤3.3.1 口径的精准度，≤3.3.1 上显示 3.4.0 口径（结算界面 + 回放文件信息）
- **Result:** 已完成

### Actions Taken
- HitMarginCompat 新增 `AccEra`（Legacy/Modern）双口径：xacc 权重表（Legacy 的 1.0 桶含 Auto，Modern 含 FailedFloor/不含 Auto）与 Accuracy 完美族（LegacyPerfectFamily = 内档完美+Auto，Modern = PerfectFamily）都按名解析到**当前枚举**——新版算旧口径时「旧 Perfect」由三档内档完美合并顶替，旧版算新口径时 FailedFloor 缺名自动缺席。`XAcc(counts, era)` / `Accuracy(counts, era)` 带口径重载；FailedFloor 的分母重复计数与 1e-4 扣减只属于 Modern（旧版没有这个判定，旧式子里也没有这一项）。零参重载保持现役口径行为不变，全部旧调用点不受影响。
- 结算界面：`Patch_CrossEraAccuracy`（Features/Results/）Postfix `DetailedResults.GenerateResults`，追加一行「另一口径」精准度——跟随 `Persistence.showXAccuracy` 同开关、同格式化函数（X-Accuracy 走 `RDUtils.XAccuracyToString`，Accuracy 走 0.00%）；xacc 的 0.9875^checkpoint 惩罚与官方 percentXAcc 同规则（编辑器试玩不罚）；判定分布为空时不加行。
- 回放文件信息：`ReplayFileInfo` 新增 `CrossXacc` / `CrossAccuracy`（Apply 里按 `CrossEra` 计算，惩罚规则与现役口径行一致）；`ReplayInfoPanel.DrawStats` 在 X-ACC 行之后加跨口径行（标签键按当前游戏版本二选一，无分布不画）。
- i18n：source.json + 12 locales 各落 4 键（playback.info.acc/.xacc 的 .legacy/.modern），gen-locdata 重新生成 LocData（490 键 × 14 语言）。
- 闸门：新增 4 条形状判据（折叠进 UiLogicShapeCheck 汇总行）+ MUT-U119~U122，四条全部实测变红且各自只红自己；verify.md 的 UiLogicShapeCheck 行已登记。
- 已知口径边界：跨口径值由**当前版本的枚举**按名解析得出——在 3.4.0 上看旧回放时，旧版不存在的档位（Midspin）按缺席处理；这是「同一分布换公式」的语义，不是「回放录制版本」的语义。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.1s） | pass |
| gen-locdata | 键集合 14 语言一致 | 490 键 × 14 语言 | pass |
| MUT-U119 | 兼容层口径判据变红 | ✓ 被检出（失败 1 条） | pass |
| MUT-U120 | 结算追加行判据变红 | ✓ 被检出（失败 1 条） | pass |
| MUT-U121 | 回放摘要判据变红 | ✓ 被检出（失败 1 条） | pass |
| MUT-U122 | 面板跨口径行判据变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓，0 失败，全部通过 | pass |

## Session: 2026-11-14 (4)

### Current Status
- **Task:** 修跨版本判定分布错位——旧版录的回放在新版上读错位（用户追问「难道 3.3.1 录的回放精准度是 3.3.1 算的？」戳中的既有缺陷）
- **Result:** 已完成

### Actions Taken
- 根因：JudgmentList 是录制时 hitMarginsCount 的原样快照（下标 = 录制版本枚举顺序），读取端按当前枚举解释；两版本枚举只有 0–3 相同（旧 Perfect = 新 PerfectMinus），4 起错位（旧 LatePerfect=4 → r150 XPerfect）。升级游戏后看旧回放：判定分布、重算 Accuracy/Xacc、跨口径行全错位；文件里存的 PercentXacc（录制版公式算的）反而是当时唯一正确的数。ReplayFileInfo 旧注释「自动对上、不需要转换」对 0–3 成立、对 4+ 不成立，已修正。
- 修法（HitMarginCompat.RemapJudgments）：按文件 SpVersion（Patch_Lifecycle 写入 "r149" 式 build 号）判录制布局；SpVersion 是后补字段、早期文件为空 → 按桶数判别（hitMarginsCount 恒为 max(枚举值)+1 全长，12 = Legacy、≥16 = Modern）；都判不出按当前布局解释。重排按名映射到当前枚举：旧 Perfect → PerfectMinus（三档拆分不可恢复，兼容层口径本就并回一个槽）；新版三档落旧枚举并回 Perfect 桶；Midspin/FailedFloor 落旧枚举无处安放则丢弃并计入日志。重排发生时记日志（铁律 1）。
- 接线：ReplayFileInfo.Apply 的 ParseJudgments 结果过 RemapJudgments（带 data.SpVersion）；分布消费点仅此一处，判定分布显示/重算/跨口径全部受益。
- 闸门：新增 2 条形状判据（折叠进 UiLogicShapeCheck 汇总行 187→189）+ MUT-U123/U124。首版 MUT-U123 没红——判据①没钉时代判定行，补上 Contains 后实测变红（教训：变异必须删掉判据的某个锚点才算数）；MUT-U124 一次检出。verify.md 已登记。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（2.9s） | pass |
| MUT-U123 | 兼容层重排判据变红 | 首跑未红（判据漏钉锚点，已补）；重跑 ✓ 被检出 | pass（补后） |
| MUT-U124 | 回放摘要接线判据变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 189 条），0 失败，全部通过 | pass |

## Session: 2026-11-14 (5)

### Current Status
- **Task:** 跨口径中旋语义裁定落地——3.3.1 把中旋命中记成 Perfect（用户记忆 + r150 源码双向核实）
- **Result:** 已完成

### Actions Taken
- r150 源码核实（用户问「中旋不算精准度你注意到了吗」）：`PlayerHitMarginWeights` 无 Midspin；`AddHitInternal` 只对 `IsPlayerHitMargin()` 自增 `playerHitMarginCount` → 中旋命中完全排除出 X-Accuracy（分子分母都不含）；`GCS.InvisibleHitMargins = { Auto, Midspin }`（中旋默认无飘字）。但普通 Accuracy 把中旋当完美（`PerfectHitMargins` 六元组含 Midspin）。
- 判定机制核实（scrPlanet.cs:789-808/913）：`midspinInfiniteMargin` 时 `hitMargin2 = HitMargin.Midspin` **顶替**原档位，一次命中一次判定——即用户说的「3.4.0 减了一个 Perfect 判定」（从 Perfect 桶挪进不参与 xacc 的 Midspin 桶），不是额外多一次判定。
- 用户裁定：3.3.1 当时把中旋记成 Perfect → 跨口径 Legacy 三处同步落地：权重表 Legacy 1.0 桶补 Midspin；LegacyPerfectFamily 补 Midspin；重排 modern→legacy 时 Midspin 并回 Perfect 桶（不再丢弃）。现代码路径（X-Acc 排除 / Accuracy 按完美）与游戏逐位一致，不动。
- 闸门：跨口径① 与 重排① 各补 Midspin 锚点 + MUT-U125（两处 ReplaceExact 同拆）实测两判据同红；verify.md 已登记。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（2.6s） | pass |
| MUT-U125 | 跨口径①与重排①同红 | ✓ 被检出（失败 2 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 189 条），0 失败，全部通过 | pass |

## Session: 2026-11-14 (6)

### Current Status
- **Task:** 用户指出没看 3.3.1 反编译代码 → 去 adofai-libs git 历史找 r148 反编译（feeed7081，releaseNumber=148），实测校正 Legacy 口径
- **Result:** 已完成

### Actions Taken
- 定位：adofai-libs 的 auto-decompile 提交里，feeed7081（2026-08-14）是 3.4.0 之前最后一次反编译，releaseNumber=148（注：是 r148；若 3.3.1 = r149 则无该版反编译，按 r148≈旧口径校准）。
- r148 实测三处与兼容层旧注释不符，全部校正：① 旧 X-Acc 分母 = hitMargins.Count + deadTiles（全部判定条数含 Multipress/Auto/失败），不是加权桶和——LegacyWeights 补 Multipress@0.0（只进分母）；② 旧 X-Acc 分子有 0.2×deadTiles 项——LegacyWeights 补 FailedFloor@0.2 顶替（r150 把漏砖变成了判定档，FF≈deadTiles）；③ Accuracy 的 1e-4 扣减两版本都在（r148 扣 deadTiles、r150 扣 FailedFloor）——去掉 era 门，统一用 FailedFloor。
- 旧枚举 12 桶顺序与 LegacyLayout 逐位一致（TooEarly..OverPress）；r148 已有 midspinInfiniteMargin/midSpin 砖且无 Midspin 档 → 中旋命中按正常计时判定（窗内即 Perfect），用户裁定「当时算 Perfect」获源码支持。
- 修正行为变化：r148 本机的主行（ComputePercentXaccVanilla / 文件名后缀）现在含 Multipress 分母——比旧实现更贴近 r148 真实游戏。已知限制：deadTiles 不是判定档，无法从分布完美重建（FF≈deadTiles 近似）；r148 本机原生游玩时 deadTiles 不在分布里，该项缺失（日志/注释已写明）。
- 闸门：跨口径① 补 3 个锚点 + MUT-U126 三处同拆实测变红；verify.md 登记 r148 源码出处。
- 教训：UiLogicShapeCheck 的 Check 先经 SrcUtil.Clean **剥注释**再跑判据（保留字符串字面量与缩进）——判据锚点不许包含注释文本。首版 FF 行锚点带了 `// FF` 尾巴，干净文件上也红；改为行序唯一化（FailedFloor 名单前置）的无注释锚点后修正。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误 | pass |
| UiPreview 直跑 | 界面形状判据全绿 | 189 条 ✓ 0 ✗ | pass |
| MUT-U126 | 跨口径①变红 | 首版锚点带注释被 Clean 剥掉未红，改无注释锚点后 ✓ 被检出 | pass（补后） |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 189 条），0 失败，全部通过 | pass |

## Session: 2026-11-14 (7)

### Current Status
- **Task:** 修复结算界面看不到 3.3.1 跨口径行（用户截图报告；回放信息面板正常、结算缺失）
- **Result:** 已完成，已重新部署

### Actions Taken
- 排查：Player.log 第 209 行确认 `Patch_CrossEraAccuracy.Postfix#8 → DetailedResults.GenerateResults (Postfix)` 挂载成功；同屏「最多按键使用数量：6」（≤10 键本应隐藏）证明 GenerateResults 返回值就是屏幕文本 → 行已写入字符串，但末尾追加的一行落在 TMP 可见区域之外（用户猜想「显示在屏幕外」成立）。
- 修法（Patch_CrossEraAccuracy）：Append 改 Insert——插入位置=紧跟游戏自己的精准度行（用 RDString.Get("status.results.xAccuracy"/"accuracy") 定位，与语言无关），找不到时退回文本开头；中段插入避开块尾被裁的问题。加 Debug.Log 让「行是否已插入」在 Player.log 可见。
- 过程坑：新 Insert 用 StringComparison 忘带 using System（CS0103，已补）；mutate.ps1 的 MUT-U127 注释误写 //（PS 要 #，整脚本解析失败，已改）。
- 闸门：跨口径② 补 2 锚点（text.Insert(lineEnd、status.results.xAccuracy）+ MUT-U127 实测变红；verify.md 登记插入位置判据。
- 部署：游戏未运行，deploy.ps1 EXIT=0，新 DLL（1351680B）已入 Mods\SpectreReplay。等玩家进结算界面复验（Player.log 应出现「结算跨口径行已插入」）。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误 | pass |
| MUT-U127 | 跨口径②变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 189 条），0 失败，全部通过 | pass |

## Session: 2026-11-14 (8)

### Current Status
- **Task:** 结算跨口径行两处修正（玩家截图反馈）：① 标签裸奔显示原始键名（playback.info.xacc.legacy）——补丁里忘了套 Loc.T；② 3.3.1 口径显示 4 位小数——r148 结算显示惯例是 2 位（$"{num2*100f:0.00}%"，无 XAccuracyToString）；③ 玩家反馈单独占行太占空间
- **Result:** 已完成，已重新部署

### Actions Taken
- 改为**内联**：InsertInline 把跨口径值插在游戏精准度行的 % 号后（定位用 RDString 本地化标签，语言无关），形如 `X-精准度: 98.7744% (3.3.1: 98.79%)`——不占独立行，也天然避开键名裸奔（内联标签是版本号，语言无关，不走文案表）；定位不到就记日志不加。
- 格式跟口径走：Legacy 口径两位小数（= r148 显示惯例，实测 r148 无 XAccuracyToString）、Modern 口径四位（XAccuracyToString = r150 惯例）。CrossKey 删除（内联用版本号标签，无需文案表；4 个 loc 键仍归回放信息面板使用）。
- 闸门：跨口径② 锚点更新（text.Insert(pct + 1 / EraTag）+ MUT-U127 重定向实测变红；verify.md 措辞同步。
- 部署：游戏未运行，deploy.ps1 EXIT=0，新 DLL 已入 Mods\SpectreReplay。等玩家进结算界面复验。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.0s） | pass |
| MUT-U127 | 跨口径②变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 189 条），0 失败，全部通过 | pass |
| 部署 | EXIT=0 且目标目录有效 | deployed: Mods\SpectreReplay，DLL 1351680B | pass |

## Session: 2026-11-14 (9)

### Current Status
- **Task:** 用户裁定「跨口径对照显示不做」→ 移除显示层（结算内联 + 回放面板两行 + 跨口径字段/文案键），保留三项与显示无关的源码实证修复
- **Result:** 已完成，已重新部署

### Actions Taken
- 移除：Patch_CrossEraAccuracy.cs（结算内联补丁）、ReplayFileInfo 的 CrossXacc/CrossAccuracy 字段与 Apply 计算、ReplayInfoPanel 跨口径行与 CrossAccKey/CrossXaccKey、4 个 loc 键（playback.info.acc/xacc 的 .legacy/.modern，LocData 重生成 486 键 × 14 语言）。
- 兼容层收敛回单表结构（去掉 AccEra 参数化重载/CurrentEra/CrossEra/EraWeights），**保留全部源码实证校正**：r148 公式（Legacy 分母含 Multipress@0.0、Accuracy 分子族不含多压）、中旋强制 Perfect 的裁定落在重排并桶（scrPlanet 源码实锤）、SpVersion 分布重排。跨口径显示的对照价值消失后，era 参数化 API 成死代码，一并清理。

## Session: 2026-11-15

### Current Status
- **Task:** 修 bug：渲染成片「新球飘移」（命中判定改用越界帧触发）
- **Result:** 已实现，待实机 A/B 复验

### Actions Taken
- 根因（游戏源码实锤，scrPlanet.cs:379）：星球角度是**歌曲位置的导出值**（`angle = snappedLastAngle + (songposition − player.lastHit)/crotchetAtStart × π × speed × 方向`，Update_RefreshAngles），且刷新跑在 scrController.Update 之后——命中排水在 Postfix 里读 `chosenPlanet.angle` 字段拿到的是**上一帧**旧值，命中固定晚一帧：越界帧先把旧轨道「冲过砖块」渲染出去、下一帧才命中弹回录制角度，快歌下就是成片里肉眼可见的新球飘移。
- 修法（越界帧判定）：新增 `Patch_PlayerControlUpdate.GetAngleThisFrame`，按与游戏刷新**同式同常数**（含 float π 字面值 3.1415927410125732）用本帧歌曲位置现算将要渲染的角度，排水两处读点（初读+重读）换用之——命中恰好在越界帧触发，越界帧画面由既有子帧回拨钉到录制角度，冲出→弹回消失。渲染期 songposition_minusi 是钉扎等步长值，导出式零估计误差，pitch≠1 也不偏（deltaTime 差分近似做不到）。
- 刻意不做：判定替换（FeatureParityCheck 禁）、星球专用时钟（MUT-U131/U132 守）、判定钳制（MUT-U133 守）、钉扎相位偏移（MUT-U130 守）——本改动只对齐命中触发帧，不碰这些形状；子帧回拨保留（它把命中帧渲染钉到录制角度的角色不变）。
- 闸门：⑬ 更新（锚点 `GetAngleThisFrame(__instance)` + 新增「禁 `float angle = GetAngle(__instance);` 字段直读」判据）+ MUT-U134 变异；verify.md CrossPlatformInputCheck 行登记。

- 过程坑两处：① 收敛时误删 AccEra 枚举（DistributionEra 还在用，CS0246 已补回重排区）；② 修复 CurrentEra 残留时把 if 整行换成了注释，重排被无条件跳过且 CS0162 被增量编译掩盖——直跑 UiPreview/读文件核对后修正（教训：增量构建的 0 警告可能是没重编译，锚点编辑后必须核对实际文件内容）。
- 闸门重整：删 4 条跨口径判据，新增 1 条「Legacy 口径公式按 r148 反编译校正」（Multipress 只进分母/分子不含多压/扣减无条件/分母时代规则）+ MUT-U126 重定向实测变红；U119 重定向（分母 HasXPerfect 规则）、U125 收敛（只钉重排并桶）、U120/U121/U122/U127 删除；verify.md 措辞同步（显示裁定注明「公式校正保留」）。
- 部署：游戏未运行，deploy.ps1 EXIT=0，清理后 DLL（1348096B）已入 Mods\SpectreReplay。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误 | pass |
| gen-locdata | 键集合 14 语言一致 | 486 键 × 14 语言 | pass |
| MUT-U126 | Legacy 校正判据变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 186 条），0 失败，全部通过 | pass |
| 部署 | EXIT=0 且目标目录有效 | deployed: Mods\SpectreReplay，DLL 1348096B | pass |
| 部署 | EXIT=0 且目标目录有效 | deployed: Mods\SpectreReplay，DLL 1351680B | pass |

## Session: 2026-11-14 (10)

### Current Status
- **Task:** 恢复跨口径展示（用户澄清裁定范围）——「不做」指 3.3.1 本机不显示 3.4.0 口径，**不是**撤掉 3.4.0 上的 3.3.1 口径展示
- **Result:** 已完成，已重新部署

### Actions Taken
- 澄清误会：session (9) 把「不做这个」理解成了撤掉整个跨口径显示，实际用户只是要去掉 3.3.1 本机显示 3.4.0 口径的方向（主用例「3.4.0 显示 3.3.1」要保留）。
- 恢复（单向收敛版）：兼容层回双表（LegacyXAccWeights/ModernXAccWeights，Legacy 表含 Midspin@1.0/FF@0.2/Multipress@0.0 的 r148 校正）+ LegacyXAcc/LegacyAccuracy 单独出口；Patch_CrossEraAccuracy 重建（HasXPerfect 门 + 结算精准度行内联 (3.3.1: xx.xx%)，Legacy 两位=旧显示惯例）；回放面板跨口径行恢复（仅 3.4.0 本机渲染：字段 -1 初始化 + HasXPerfect 门）；文案键恢复 2 个（.legacy，LocData 488 键 × 14 语言）。
- 3.3.1 本机不展示：补丁 HasXPerfect 门 + 面板字段 -1 不画 + 无 .modern 文案键三重收敛。
- 闸门：跨口径②③④ 恢复（锚点改 HasXPerfect 门/LegacyXAcc/LegacyAccuracy/两位格式）+ 重排① 补 Midspin 权重行锚点 + MUT-U125 补回权重行 ReplaceExact（缩进修正：8 空格非 12）+ MUT-U120/121/122 恢复——5 个变异全部实测变红；verify.md 措辞更新为「仅 3.4.0 展示」。
- 部署：游戏未运行，deploy.ps1 EXIT=0，新 DLL（1351168B）已入 Mods\SpectreReplay。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.1s） | pass |
| gen-locdata | 键集合 14 语言一致 | 488 键 × 14 语言 | pass |
| MUT-U120 | HasXPerfect 门判据变红 | ✓ 被检出（失败 1 条） | pass |
| MUT-U121 | 回放摘要判据变红 | ✓ 被检出（失败 1 条） | pass |
| MUT-U122 | 面板跨口径行判据变红 | ✓ 被检出（失败 1 条） | pass |
| MUT-U125 | 重排①+Legacy 校正同红 | ✓ 被检出（失败 2 条） | pass |
| MUT-U127 | 结算内联判据变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 189 条），0 失败，全部通过 | pass |
| 部署 | EXIT=0 且目标目录有效 | deployed: Mods\SpectreReplay，DLL 1351168B | pass |

## Session: 2026-11-15 (1)

### Current Status
- **Task:** 结算内联标签瘦身（用户截图反馈「为什么要这么长就不能直接写3.3.1」）—— Loc.T 完整标签改为纯版本号
- **Result:** 已完成，已重新部署

### Actions Taken
- InsertInline 去掉 Loc.T 完整标签查表（"X-Accuracy（3.3.1 口径）"），内联改为 ` (3.3.1: 96.97%)` 纯版本号形式——语言无关、无需文案表；面板行仍用完整标签（空间充足）。MUT-U127 目标行同步。
- 顺带发现：游戏 X-精准度主行的小数位不固定（98.7744% 四位 / 96.85757% 五位）——XAccuracyToString 的行为，与跨口径无关。
- 部署：游戏未运行，deploy.ps1 EXIT=0，新 DLL（1356800B）已入 Mods\SpectreReplay。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.8s） | pass |
| MUT-U127 | 结算内联判据变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 191 条），0 失败，全部通过 | pass |
| 部署 | EXIT=0 且目标目录有效 | deployed: Mods\SpectreReplay，DLL 1356800B | pass |

## Session: 2026-11-14 (11)

### Current Status
- **Task:** 跨口径展示加设置开关（用户需求「用一个开关控制显示」）——照 AlwaysShowMaxUsedKeys 完整接线样板
- **Result:** 已完成，已重新部署

### Actions Taken
- 接线六件套：FeatureKeys.CrossEraAccuracy 常量、Settings.CrossEraAccuracy（默认 true）、ConfigManager DTO 属性 + 存/取两行、Main.SetFeatureToggle 注册、回放页 OnGui.Row（replay.crosseraacc，在 maxusedkeys 行之后）。
- 门控：Patch_CrossEraAccuracy 属性加 Feature = FeatureKeys.CrossEraAccuracy（关=hub 卸补丁）；ReplayInfoPanel 跨口径行加 Settings.CrossEraAccuracy 门（注意括号：(A && (B || C))，否则 OFF 时 || 分支漏光）。
- 文案：replay.crosseraacc + .tip（14 语言，LocData 490→492 键）。
- 闸门：跨口径② 补 Feature 门锚点、④ 补 Settings 门锚点（首版锚点没跟上加门后的行文——U128 连带④红暴露后修正）+ 新增 2 条接线判据（Main.SetFeatureToggle / Pages 设置行）+ MUT-U128（Main 谓词 → false）/U129（设置行 → 常量 false）实测变红；verify.md 措辞更新。
- 过程坑：U128 首跑连带④红——④锚点未同步 Settings 门行文（增量构建无警告掩盖）；修正后 U128 只红接线判据。
- 部署：游戏未运行，deploy.ps1 EXIT=0，新 DLL（1356800B）已入 Mods\SpectreReplay。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误（3.4s） | pass |
| gen-locdata | 键集合 14 语言一致 | 492 键 × 14 语言 | pass |
| MUT-U128 | FeatureToggle 接线判据变红 | ✓ 被检出（首跑连带④红系④锚点过时，已修正） | pass |
| MUT-U129 | 设置行判据变红 | ✓ 被检出（失败 1 条） | pass |
| verify -Ci | 非 0 断言且 0 失败 | 206 条 ✓（界面形状汇总 191 条），0 失败，全部通过 | pass |
| 部署 | EXIT=0 且目标目录有效 | deployed: Mods\SpectreReplay，DLL 1356800B | pass |

## Session: 2026-11-15 (2)

### Current Status
- **Task:** 0.3.2 版本发布——修成片「新球飘移」
- **Result:** 已发布（commit 4c62f79，tag v0.3.2，已推送触发 CI）

### Actions Taken
- 修复：命中判定改按本帧歌曲位置现算星球角度（GetAngleThisFrame，与刷新同式同常数），越界帧当帧触发 + 子帧回拨钉到录制角度——成片「新球飘移」消除；闸门⑬同步（禁字段直读/必须现算）+ MUT-U134。
- 新增星球平滑度记账/弧段探针（渲染完成打日志，验证修复的量化仪）；沉淀六版时钟实验终局（星球读钉扎原值、钉扎纯等步长）+ MUT-U130~U134 防复活。
- 附带实现过「镜厅罩 UI」功能（对照参照实现的越界帧判定语义），实测效果不达预期后整体 revert（58131b7），技术结论留档：ScreenSpaceOverlay 不经相机、镜厅累积罩不到 UI；迁 UI 需不清屏专属相机 + 子树层覆盖 + 动态画布补扫。
- 发布流程照 §7.8：三处版本文件 0.3.2 → 构建 0 警告 → 部署 + verify 全绿（含部署一致性与上游检查）→ 纯版本提交 4c62f79 → 附注 tag v0.3.2 → push origin master v0.3.2。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误 | pass |
| verify 全量 | 全绿含部署一致 | 全部通过 | pass |
| MUT-U130~U135 | 各自实测变红 | 全部检出 | pass |
| tag v0.3.2 | 附注+SSH 签名 | HitMargin 签名附注 tag | pass |
| 推送 | master + tag | 58131b7..4c62f79、[new tag] v0.3.2 | pass |

## Session: 2026-11-18

### Current Status
- **Task:** 2C 硬件矩阵实机收口 → 用户裁定为危险任务（会烧毁显示屏），撤销
- **Result:** 已完成撤销与文档落库

### Actions Taken
- `task_plan.md` Phase 9：「2C QSV/AMF 全矩阵」「硬件矩阵实机收口」「硬件 HDR10 实机」三项标 `[撤销]`；Next Step 改为「只剩 Pages 拆分与静态状态收敛」。
- `tools/ffmpeg-probe.ps1`：删除硬件矩阵段（NVENC/QSV/AMF × H.264/H.265 × 8/10bit 的真硬件编码会话，78 行），原位留注释说明禁止重写；软件参数 / 10-bit / HDR10 / AV1 用例不变。
- `AGENT.md` §1 硬约束表新增「危险任务（禁止执行）」行，把这条钉成红线。
- `findings.md` 新增裁定记录：QSV/AMF 永不写「已验证」，只保留离线形状断言。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| 脚本解析 | 0 错误 | `[Parser]::ParseFile` 0 错误 | pass |
| 产品代码 | 未触碰 | 只改 `tools/` 与文档 | pass |

## Session: 2026-11-18 (2)

### Current Status
- **Task:** Pages 拆分重构（按职责拆组件、同步引用路径、保持行为不变）
- **Result:** 已完成；构建 0 警告，离线套件 315 条 0 失败

### Actions Taken
- 3371 行 `Pages/Pages.cs` 按页拆成六个 partial 文件：`RecordPage`（447 行）、`PlaybackPage`（1288 行）、
  `RenderPage`（582 行）、`EffectsPage`（228 行）、`AboutPage`（301 行）、`CloudPanel`（391 行）；
  共享壳（载入标记、待确认态、列表缓存、`Commit`/`FlushPending`/`ReplayList`）留在 `Pages.cs`（278 行）；
  `ReplayInfoPanel.cs`（419 行）为既有独立类，不在 `partial class Pages` 与 `Pages\Pages.cs` 逻辑路径内。
  逐行核对覆盖：原 `Pages.cs` 全部逻辑行全部分配且无重叠，纯行段搬移，仅三处删除按钮抽成 `ConfirmButton.Draw`。
- 抽组件 `Ui/ConfirmButton.cs`：回放删除、云端删除、预设删除三处的「确认态换文案 + 换样式 + 实测宽度 + tip + 点击判定」
  共用一份；待确认态与两段式判定留在各调用点（闸门 ㉒b 按方法体形状判，抽走就失去覆盖）。
- 闸门同步：`SrcUtil` 新增 `PagesSelector`/`PageFiles`，`Pages\Pages.cs` 变成**逻辑路径**，
  `UiLogicShapeCheck`/`PortedCompletenessCheck`/`LocCheck` 及各检查的 `Read` 全部走它；
  `mutate.ps1` 把同一路径当页面集合（`Backup` 全量、`Lines`/`SetLines` 边界行拼接、`ReplaceExact` 仍要求整集合命中 1 次）。
- `settings-ui-audit.ps1` 的界面文件集合从 `Pages.cs` 改为整个 `Pages` 目录。
- 顺带修 `LocCheck` 真误报：`Loc.T("playback.info.err." + info.Error)` 的前缀被当成键。
- 断言数拆分前后一致（315 条），未出现「拆分后断言变少」的假绿；`MUT-D6` 实跑变红并自动还原。

### Test Results
| Test | Expected | Actual | Status |
|------|----------|--------|--------|
| Release 构建 | 0 警告 0 错误 | 0 警告 0 错误 | pass |
| 离线套件 | 0 失败且条数与拆分前一致 | 315 条，0 失败 | pass |
| MUT-D6（Pages 锚点） | 变红且自动还原 | 失败 1 条，已还原 | pass |
| 只读审计 | 全过 | 全过 | pass |
