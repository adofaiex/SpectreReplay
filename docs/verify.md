# 离线验证清单

跑一遍：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1
```

四个参数：`-SkipPreview` / `-SkipDeploy`（游戏在跑时 dll 会被锁，
用 `-SkipDeploy` 跳过部署那一节）/ `-SkipAudits`（跳过只读审计那一节）/
`-Ci`（跳过两项依赖本机安装的检查：Steam 部署一致性、上游工作区干净。

> ⚠️ **`-SkipDeploy` 只跳过「部署动作」，不跳过后面的 sha256 比对。** 真没部署过时它必然红，
> 看着像代码坏了。那种机器上要跑绿得用 `-Ci`；语义上正确的做法是「要么真部署一次，
> 要么用 `-Ci`」，别用 `-SkipDeploy` 假装跳过。
> CI 里这两项不是"没部署"就是"目录不存在"，长期假红会淹掉真问题；
> 其余各项在 CI 里照常全跑）。
>
> **2026-10-25 更正上面这条的旧解释。** 它原先写的是「本机从没部署过（`Mods\SpectreReplay` 为空）」，
> 那个诊断是错的，而错的诊断比没有诊断更糟——它让这一项看起来天经地义地该红，
> 于是长期用 `-Ci` 跳过，**唯一的实机一致性检查等于常年关闭**。真实情况是：
> 本机 UMM 0.33 扫描的模组目录是 `<game>\UMMMods`（见 `UnityModManager\Log.txt` 的
> `[Manager] Mods path:` 一行），而四个脚本各自把路径写死成了 `<game>\Mods\SpectreReplay`。
> 于是部署脚本一直在写一个没人读的目录（空操作），比对也一直在查一个不存在的 dll。
> 四处各写一份**就是这件事能发生的根因**，现在路径解析只剩 `tools\_gameroot.ps1` 一份：
>
> - 解析顺序：`-ModsRoot` 参数 → `SRM_MODS_ROOT` 环境变量 → `Mods` → `UMMMods` → 默认 `Mods`；
> - 判据是「该目录下有至少一个 `*\Info.json`」，**不是「目录存在」**——本机 `Mods`
>   目录是存在的、只是空的，只判存在会挑中一个空目录，症状与本 bug 一模一样；
> - `deploy.ps1` / `verify.ps1` / `ffmpeg-probe.ps1` / `convert-probe.ps1` 全部点源引用它。
>
> 另：部署后若目录里残留 `SpectreReplay.dll.<pid>.cache*`，闸门现在会**告警**——
> UMM 启动时优先加载那个缓存，缓存没清掉时"dll 已更新、游戏仍跑旧代码"是最难查的形态。

> **控制台输出必须按 UTF-8 解码**，`verify.ps1` 开头已显式设置。
> 2026-10-19 实测过一次：Windows PowerShell 5.1 用 OEM 码页（936/437）
> 读 `dotnet` 的 UTF-8 输出，`✓`/`✗` 全成乱码，闸门印出
> **「断言 0 条，失败 0」**——与全绿肉眼无法区分，整套离线套件等于没跑。
> 现在「0 条断言」本身判红，并贴出原始输出末尾用于定位。

六件事绑成一次判定，任何一项红就返回非零：脚本编码、主项目构建（警告也判红）、
离线验证套件、**只读审计**（6 个离线审计脚本）、本仓库工作区干净、
线上 dll 与构建产物的 sha256 一致、上游 Spectre 工作区干净。
（后两项只在本地跑，`-Ci` 跳过；上游路径优先读 `SPECTRE_UPSTREAM`。）

> **当前打印 404 条断言 0 失败**（2026-11-23 离线套件实跑；14 语言 × 523 键）。
> 这个数字会随轮次变，引用前先跑一遍 `verify.ps1` 看真实条数。
> **注意它是「✓ 行数」而不是判据数**——`UiLogicShapeCheck` 现有 195 条判据只印 1 行，
> 所以加判据这个数字不变。

## 套件构成

闸门在 `tools\UiPreview`，`Driver.cs` 汇总所有 `*Check.cs`。**新增 .cs 检查文件
会被 `EnableDefaultCompileItems` 自动收进来，但必须在 `Driver.cs` 里登记
`<类名>.Run(...)`**——不登记就是孤儿检查，一次都不跑而闸门只会更绿
（`PortedCompletenessCheck.NoOrphanChecks` 守这件事，变异 `MUT-L2`）。

| 组 | 验的是什么 |
|---|---|
| LocCheck | 页面引用的键在两种语言下都齐；两张表键集合一致；中文/英文都能解析成文案、不返回键名本身 |
| HardTextCheck | 没有被写死的用户可见中文；文案表文件里没有漏网的中文字符串 |
| ReplayCheck | 链接真实 ReplayEncoder/Decoder 的回放格式往返（五种数据形状含巨大 count 被干净拒绝；**巨大 count 必须由 ReadCount 守卫拒、异常消息要核对**——2026-10-19 修，此前用裸 gzip 解新格式，在解压阶段就抛 InvalidDataException 被当成「已拒绝」，MUT-P2 把守卫全短路也照样绿；**⑹ v1 旧回放（41 B 记录）仍能解码**——守卫的最小记录长度必须按段版本取，合并成 42 会让所有 r149 及更早的回放被判「文件损坏」，2026-10-24 加） |
| WavRoundTripCheck | 键盘音 WAV 的写读往返：写侧头部契约**取自真源码**、读侧接受范围覆盖写侧产出、拒 6 种坏头（非 PCM/位深 12,64/声道 0,负/FormatTag 0）、四种合法位深全接受、位深下限常量与判定一致、**写侧多声道必须下混成单声道（⑥，2026-10-19 加）** |
| LocRuntimeCheck | 两个方向：**正向**——被取词的运行时键（`GetLocalizedText` 字面量 + `SetPhase` 第二实参）在全部 14 种语言下都齐；**反向**——表里定义的键必须真有人取，死键即为红（2026-10-17 加，此前表里有 315 个键、真正在用的只有 33 个） |
| PortedCompletenessCheck | 移植搬来的模块都真有调用点：ReplayAnalyzer 显示、DescribeEncoder 降级提示、playback.ready 装弹提示、VideoConverter 入口、**转换目标来自 `Settings.ConvertTarget` 且必须可编辑 + 双向持久化（⑦/⑦b/⑦c）**、回放列表走集中清单且只含新格式两种(⑧/⑧b/⑧c)、键盘音三项有界面入口（⑨a/⑨b）、回放驱动开关有界面入口（⑩/⑩b）、保存目录有界面入口（⑪/⑪b）、**「打开这一关」按钮（⑫/⑫b）+ 编辑器跳转等就绪后定位起始砖（⑬）+ 命中分布图复用缓存（⑭）+ 渲染时钟开关四件齐（⑮a~⑮f）**、**去特效总开关默认关（⑯）+ 中文文案不得把关卡称作存档（⑰）+ 键位重映射有总开关/对照表/写回三步（⑪c~⑪e）**、没有孤儿检查 |
| UiLogicShapeCheck | 界面控制流形状：停止录制不许借用「取消保存」文案、文本框必须**自绘**（不许用内建输入框）且宽度走 Gui.CtrlW、分组边框不许用 GetLastRect、**阻塞式原生目录选择框必须 Defer 到 OnGUI 之外（两个调用点各一条）**、**输入框底色必须比卡片更亮**、**按钮必须传自定义样式 + 滑条必须自绘 + 圆角贴图必须有缓存**（2026-10-12 用户报「UI 还是原生控件」后加的）、**混音轨 Render 容器必须精确**（⑧）、**卡片不许用 GUI.backgroundColor 染色**（⑨，乘算会抹平按钮 hover）、**更新检查三态不许合并 + 版本比较方向 + 读 ModEntry.Version + info.json 两个字段非空**（⑲a~⑲d，2026-10-17 加）、**拨开关必须当场刷新编辑器保存按钮**（⑳a/⑳b，2026-10-18 用户报「允许覆盖关卡开关后不会实时生效」）、**特殊按键两条输入路径都挂 Feature + Main 已注册 + Escape 不得放开**（㉑a~㉑c，2026-10-18 加）、**独立窗口投影必须用 9-slice 样式绘制**（⑬c18；2026-11-09 用户报阴影缺失，参考 StutterFix，`MUT-U107` 守）、**纯净相机渲染必须隐藏游戏判定飘字**（`ShowHitText` 常驻前缀只拦 CleanCamera + `CurrentSurface` 随帧源装/卸；2026-11-14 用户报成片全是判定文本，`MUT-U117`/`MUT-U118` 守）、**跨口径精准度标注（仅 3.4.0 展示 3.3.1 口径）**（结算精准度行内联 (3.3.1: xx.xx%)——Legacy 两位=旧显示惯例 + 回放信息面板跨口径行；3.3.1 本机**不展示** 3.4.0 口径（2026-11-14 用户裁定收敛）；`MUT-U120`/`MUT-U121`/`MUT-U122`/`MUT-U127` 守）、**跨版本判定分布重排**（分布按录制版枚举顺序存、按当前枚举读会错位；SpVersion/桶数判布局 → 按名重排到当前枚举，`MUT-U123`/`MUT-U124` 守）、**Legacy 口径公式按 r148 反编译校正**（分母=全部判定条数含多压（Multipress@0.0 只进分母）、Accuracy 分子不含多压、1e-4 扣减两版本同在（r148 扣 deadTiles——非判定档、无法从分布重建，旧口径缺失已记录）；中旋裁定（3.3.1 强制记 Perfect——r148 scrPlanet 源码实锤）保留在重排并桶；2026-11-14 用户裁定不做跨口径对照显示，公式校正保留；r148 源码 = adofai-libs feeed7081 CalculatePercentAcc，`MUT-U119`/`MUT-U125`/`MUT-U126`/`MUT-U135` 守（X-Acc 分母 = hitMargins.Count 全部判定条数，OverPress@0.0 与 Multipress 一样只进分母））、**星球时钟链路保持删除**（六版实测终局：v1/v2 钉扎层补偿=反馈回路整体推迟一帧、v3 方向反、v4/v5 提前一帧+判定钳制=同曲同段 A/B 实测星球恒定前冲一个可视步长+「停一帧跳两帧」、观感上就是飘移；星球与判定/砖块共用钉扎值=贴砖形态，快歌单帧大步进是帧率量化物理上限、时钟层无解；`PlanetSongPosition` 桥/`PinnedFrameLength`/`PlanetClampTick`/`TryPeekNextHitTime` 全部删除，`MUT-U131`/`MUT-U132`/`MUT-U133` 守） |
| MissingRuntimeKeyCheck | 运行时键必须有定义（`GetLocalizedText` 查不到时 `return key`，玩家会看到原始键名） |
| KeybdSoundTickCheck | 键盘音起始时间戳：起录块不许导体判空后 return、必须能补真值、回放侧拒非正基准 |
| ConfigMigrationCheck | 旧配置迁移：迁移真的被调用、不误判新版文件、每个旧键都有归属 |
| PatchTargetCheck | 补丁目标在**真程序集**上存在且形参对得上；解析覆盖率 == 源码声明数 |
| ReflectionTargetCheck | 按名字取的游戏成员存在：`Notification.SetupNotification` 形参、`Time` 5 属性 getter、`AudioSettings` 3 属性 |
| LevelProbeCheck | 关卡探测判据：现判据认得出 scnGame/scnEditor；三个历史错判据仍然错；认得出 4 个非游玩场景；移植后仍用场景名不许退回 `gameworld` |
| CrossThreadFieldCheck | 9 个跨线程字段都带 `volatile`（UI 线程读、编码线程写） |
| ReplayCryptoCheck | 加密往返、密文无明文 magic、同一明文两次加密结果必须不同、篡改正文首字节/IV 被 MAC 拒、**非本模组容器一律拒绝（上游旧格式回落已整体移除，不再做试探性解密）**、v2 容器仍可解 |
| EnumParseGuardCheck | 持久化枚举带 `Enum.IsDefined` 护栏（`Enum.TryParse("99")` 会成功并给出越界值） |
| LoadMarkerCheck | `Load()` 第一句是 Debug.Log 启动行且**在 try 之前**（异常路径也带得到） |
| LocFallbackCheck | 文案回退链每一级都活着：两张表对称、缺键时静默降级到另一种语言、双缺才露键名 |
| InstanceInjectionCheck | **16 处** `__instance` 全部作用于**实例方法**（Harmony 只对非静态目标注入）。⚠️ `tools\instance-injection-audit.ps1` 因不剥注释、把 `SpectrePatch.cs` 用法示例算进去，会印 17 处——以 C# 侧这条 16 为准 |
| CrossPlatformInputCheck | 回放输入是**真注入**而不是占位：`RDInput` 三入口（GetMain / GetState / GetStateKeys）都补上、补丁体真并进返回值、**去重绝不许看「另一条路的状态」**（`KeyboardSimulation.Available` / `Application.isFocused` 都不得出现在 `ManagedInput.Active` 里）、`holdKeys` 必须是真实 `AsyncKeyCode`（不许 `AnyKeyCode(null)`、也不许 `KeyCode`——装箱判等恒不等，每帧被剥光）、**原生异步链可用时三个覆盖点必须整体放行**（否则帧粒度抹平事件的 tick 粒度，一次按键被重复消费）、**「一瞬间全部模拟完」必须钉死**（注入侧的 `dspTimeSong` 未就绪不得入队、`evTicks` 必须落在 `nowTicks` 的 ±2 秒窗口内；回放侧 `Start` 必须按当前歌曲时间对齐游标、`PumpInput` 必须有单帧事件上限并在触顶后重新对齐）、**放行的判据必须是「实际入队成功」（`Healthy`）而不是「看起来可用」（`Usable`）**——`Inject` 的每条失败分支都要记到失败帧上，否则 conductor 未建好 / pitch 为 0 / keyQueue 被换掉时，游戏那条链读到永远空的 `keyMask`，而托管层已经放行，两条路同时失效、**广播判据与「游戏是否出队」必须分开**（`Injectable` 只看钩子活着，`Usable` 额外要求 `asyncKeyboard.isActive`；后者只在 PlayerControl 为真，混用会让直接订阅 `SkyHookManager.KeyUpdated` 的键位显示模组在非 PlayerControl 状态整段失明，`MUT-CI10` 守这条）、原生接管时 `holdKeys` 必须与 `ValidInputWasReleased` 比对的那份数据同源、按下集合逐帧过期、开始/停止/跳转三处清理、非 Windows 不碰 user32、**命中推进取本帧现算角度**（`GetAngleThisFrame`：星球角度是歌曲位置导出值、刷新跑在 controller 之后，字段直读=上一帧旧值→命中固定晚一帧→越界帧先冲过砖块再弹回录制角度的成片「新球飘移」；2026-11-15 修，`MUT-U134` 守） |
| CursorTrackCheck | 指针轨迹段（容器尾部可选段）：往返（含倒计时负区间）、旧文件无轨迹段→空轨迹且旧段完好、未知尾部整体忽略、段版本上界、count 走 ReadCount 防护、插值/夹取/单样本/空表行为 |
| LogPrefixCheck | 全部日志用 `[SpectreReplay]` 前缀（真实 Spectre 模组也装着会跑，前缀是区分来源的唯一依据） |
| MuxOffsetCheck | 音画偏移（2026-11-12 加，20 行）：mux 侧 `-itsoffset` 正负两分支都存在且由偏移参数驱动（负向必须走视频侧，对音频用负 itsoffset 会把起始 PTS 推到负数被 muxer 拒收）、零偏移命令行与历史版本逐字一致、itsoffset 注入点恰好各 2 处（编码器与抢救命令）、快照必须带「-1 跟随校准」语义且在主线程落值、设置项四件套（字段/落盘/读取/界面）、抢救命令与 `MuxWith` 同参。变异 `MUT-MUX1`（正偏移分支失效）/`MUT-MUX2`（跟随分支读成 0）守 |
| ApiSurfaceCheck | 回放按键查询 API 对外契约（2026-11-12 加）：`SpectreAPI` 六个成员在位且委托同源（拉取接口必须与补丁桥同一套键码换算）、`ReplayKeyChanged` 事件与 raiser 同在、`ManagedInput.Note` 在**状态写入之后**发事件、`GetHeldReplayKeys` 无回放分支永不返回 null、两参 Raise 走主线程封送 + 逐订阅者异常隔离、`ApiVersion` 随成员升级。变异 `MUT-API1`（Note 不再发事件）/`MUT-API2`（无回放分支返回 null）/`MUT-API3`（事件成员删除）守 |
| ReplayInputBridgeCheck | ReplayInput 推桥接线（2026-11-12 加，跨 KV 通用约定）：Start/Stop/PumpInput 边沿/Rebuild 重播种/Main 复位五处在位、**按类型名 `ReplayInput` 扫描全部程序集（不许绑死某一家程序集——同类 mod 多、接口通用）**、四方法全解析、VK→KeyCode 走 SkyHook 逆链（与 `KeyViewerBridge.ToNativeVk` 互逆）且保留 VK_APPS=93 ↔ KeyCode.Menu 特判。这类 KV 受控模式只认显式调用，桥断在游戏里毫无异常。变异 `MUT-RI1`（不喂边沿）/`MUT-RI2`（不发 OnEndInputs）守 |
| ReplayTimingContextCheck | 设置页分布图缓存含样本和窗口指纹，卸载清图。`MUT-U142` 守 |
| EffectSaveGuardCheck | 链接真实临时保护代码，六组故障注入覆盖六个必需钩子返回空、非空但未实际挂载、单 Harmony 幂等与按 owner 释放、动态第二 Harmony 的真实跨程序集反射卸载、卸载无效时拒绝成功及残留回调失能、安装/回滚双失败和恢复。仅使用内存补丁记录，不创建真实 detour。配 `MUT-SG01` 至 `MUT-SG07`，本轮实跑逐条核对源码还原哈希。 |
| ReviewRegressionCheck | 审查回归 38 组：真实文件写入验证恢复备份独立、备份失败保留旧文件；去特效后编辑冲突保护、禁用 Mod 后最终 `SaveLevel()` 与 `RDFile.WriteAllText` 路径保护、另存新路径放行、重载解除及撤销/重做对象快照校验；HDR10 组合、尾帧排空守恒、默认全长选区、末击 EndTile 证据；真实编码参数验证 AV1 冷目录、QSV/SVT/AMF 码控与实际软硬件区分；内存目录与预探测缓存验证仅硬件 AV1、pending、软件回退及预启动等待预算；配置快照、首次作业保存重试起点、普通关卡重开、忙碌启动返回值、文本草稿提交及公开旧签名。云恢复复制备份、缺尾帧分支退出、最终保存入口、对象快照复制、当前对象切换，以及 Mod 卸载后新建撤销副本的保护传播都限定在本方法/分支。真实硬链接和扩展路径验证别名拦截，覆盖原路径移除/替换、独立文件放行和目标探测失败；普通保存及复制钩子均挂载后才释放临时保护。同编辑器成功加载当前关卡后直接刷新按钮，仅当前对象和撤销/重做栈均无保护时释放临时 owner；失败加载和非当前关卡不触发清理。RV46 独立验证遗漏重做栈保护，不再重复 RV44 的复制钩子变异。配 `MUT-RV01` 至 `MUT-RV56`，包含关键动作的删除和反向变异。不运行硬件编码或 HDR 探针。 |

**总数以 `verify.ps1` 实跑为准**（2026-11-23 离线套件实跑 404 条 `✓` 行、0 失败；CI 闸门仅因预期的脏工作区返回 1）。
这个数字会随轮次变；引用前先跑一遍。**它是「✓ 行数」而不是判据数**，
`UiLogicShapeCheck` 的 195 条判据只印 1 行，所以加判据这个数字不变。

> **口径修正（2026-10-12）：`✓ 行数 ≠ 断言单元数`，早先"两者相等"的说法是错的。**
> 实测有两处**折叠**：`PortedCompletenessCheck` 有 15 个判据却只印 2 行
> （「随移植搬来的模块都已被接上」+ 孤儿检查），`UiLogicShapeCheck` 有 **176 个判据**
> 只印 1 行（「68 条界面形状判据都成立」）。其余 *Check 基本一行一判据。
> 于是加判据**不会**让 `mutate.ps1` 印的「断言 N 条」变大——2026-10-12 加了
> ⑪/⑪b/④ 三条后该数字仍是 57，而三条都由 MUT-P12/P13/U6 证明会红。
> **引用条数时说明是"✓ 行数"还是"判据数"，别把前者当日志里的真值。**

每加一条断言，**必须用 `tools\mutate.ps1 -Which MUT-X` 证明它会红**（自动还原）。
已累计 10 条以上假断言、2 条假变异——形态见 `docs\SPECTRE_COMPARISON.md`
与记忆库的「六条铁律」。`-ExpectGreen` 是反向对照（证明"全绿"本身可达）。
`tools\mutate-regression.ps1` 全量回归 **298 条**变异（2026-11-16 实测；以脚本打印的 M 为准。计数口径：`mutate.ps1` 里 `'MUT-XXX' {` 块的去重数，**标签允许小写**，`MUT-K4b` 就是这个形式；2026-10-23 实测 150 个唯一名（另有 3 块把 `}` 与标签写在同一行，粗扫会数成 153）；本轮加 `MUT-CI1`~`MUT-CI10` 守跨平台回放输入，`MUT-CI10` 专守「广播判据不能用 Usable」；`MUT-KM1`/`MUT-KM2` 守键位重映射界面的调用形状与写回三步；`MUT-V15`/`MUT-V16` 守 `LimitKey` 真的改变映射表末态（分别对应历史上让它失效的两道兜底循环）。这个数字一直在漂：10-19 加 `MUT-W2` 后记成 117，加 `MUT-D15`/`MUT-D16`/`MUT-CR4` 后记成 120，加 `MUT-D17`/`MUT-D18` 后记成 122——**三次记录都比当时的真实块数小**，因为写文档时只数了「这轮新增的」。所以别再往上加着数了：跑 `mutate-regression.ps1` 时它会自己打印「通过 N / M」，以那个 M 为准。`MUT-D15` 守悬停说明必须以鼠标为锚点、`MUT-D16` 守详情面板同时显示 Accuracy 与 X-Accuracy、`MUT-CR4` 守 v2 容器仍可解密（迭代次数回落）、`MUT-D17` 守镜头缩放滑条必须挂缩放开关（否则拖了不生效）、`MUT-D18` 守去特效三颗「全选本组」按钮都挂总开关（只改最后一处，防判据只认首次命中）、`MUT-D19` 守按钮只许长在 visual/track/planet 三组（把 visual 那颗搬进 audio，总数不变）、`MUT-D20` 守全选必须双向（第一颗退回单向）、`MUT-D21` 守「移除全部装饰」挂两个父项、`MUT-D22` 守等待回放的预算必须按真实秒数记（改回 `fps * 10` 帧计数就红）、`MUT-D23` 守等待放行必须承认倒计时阶段（把 `States.Countdown` 改成别的状态就红，成段缺 GET READY / 3 2 1 / GO）、`MUT-D24` 守倒计时长度必须按谱面派生（把拍数写死成 4，等于假设所有谱都是 4 拍 145 BPM，慢歌长倒计时又会切掉开头）、`MUT-D25` 守倒计时放行不许再出现绝对秒数窗口（把 `-2.0` 下界加回放行条件就红）、`MUT-D26` 守「录制鼠标指针」开关必须接 `RenderCursorOn`（改成接回 `RenderClockOn` 就红）、`MUT-D27` 守渲染作业必须把开关交给画面源（写死 `true` 就红）、`MUT-D28` 守没有指针设备时不许画箭头（删掉 `Input.mousePresent` 就红）、`MUT-D29` 守鼠标指针叠加必须默认关（改回 `= true` 就红——它是往画面里加东西，不是录得更全）。新增 `MUT-V11` 守「Render 容器必须精确尺寸」；
`MUT-V12`/`MUT-V13` 分别守判据⑨的两半（卡片底图必须是卡色贴图、
不许再出现 `GUI.backgroundColor` 赋值——乘算会抹平按钮 hover）。
2026-10-17 另加两条：`MUT-U34` 守「去特效总开关必须默认关」（⑯）、
`MUT-U35` 守「中文文案不得把关卡称作存档」（⑰）。
`MUT-K3` 守 LocRuntimeCheck 的反向那一半（往表里塞一条没人取的键 ⇒ 必须红）。
`MUT-K4`~`MUT-K7` 守更新检查的几条判据（⑲a 已最新必须依据「查过没有」而非 `newest != null`、⑲a2 必须真读 `Params.LastUpdateCheck`、⑲b 比较方向、⑲c 读 ModEntry.Version、⑲d info.json 两个字段非空）。

2026-10-18 另加五条：MUT-U40（⑳a 删掉「允许覆盖关卡」拨动后的刷新）、MUT-U41（⑳b 总开关只留「关掉→恢复」那一半）、MUT-U42（㉑a 摘掉异步那条补丁的 Feature）、MUT-U43（㉑b 删掉 Main 里的 SpecialKeys 注册）、MUT-U44（㉑c 删掉键盘侧的 Escape 守卫）。

2026-10-24 另加三条：MUT-U45（⑧c 把旧扩展名塞回可读清单）、MUT-U46（⑧b 把 `.psprp` 从可读清单删掉）、MUT-U47（**⑳c** 给保存拦截挂回 `Feature` = 让「改写残留态在功能关闭后仍被拦住」随总开关卸载，专守 D-F2 的修复；当时 111 条断言里恰好它变红）。

> ⚠️ **MUT-U42 修过一次假变异**：第一版把参数改成 ...SpecialKeys_DISABLED，而原串仍是它的**前缀**，子串计数判据照样命中 ⇒ 代码改了、闸门没红，被误判成「判据是假的」。现在两边都收紧：判据用 CountClosedArgs（要求实参后紧跟 ) 或 ,），变异整段删参数并自带「是否真的改动了字节」的自检。与本项目早前 playback.openlevel 前缀假判据是同一形态。

> **⑲a 的判据本身改过一次，值得记下来。** 第一版要求「已最新」挂在 `newest != null` 上，
> 它能通过，但守的语义是错的——反编译 IL 证明 `ParseRepository` 只在
> `current < release` 时才 `stfld NewestVersion`，**版本相等时保持 null**。
> 于是「已最新」永远亮不起来，界面一直停在「未检查」，玩家刚查完也看不到结果。
> 判据若只验「三态文案都出现过」，就会替这个错误设计背书。
> 现在钉的是**分界条件本身**（必须是 `checkedOnce`）。这是「判据不能只验存在、要验语义形状」的又一例。

> **变异锚点会跟着代码改而失效，且失效的样子是 exit 2 而不是红。** 删掉
> `GUILayout.TextField` 那次，MUT-U2 的锚点（`TextField` + `CtrlW` 那一行）
> 直接命中 0 次、脚本 `throw`——闸门没红，是变异自己跑不起来。改完生产代码
> 记得回头核对被它钉住的那几条变异名，`-Which` 单跑一遍。

> **MUT-PORT1 已随 port-diff-audit 一并移除（2026-10-30）。** 那套「与上游逐文件
> 比对 + 差异挂记录」的移植保真度门禁退役了：本模组与上游 Spectre 早已大幅分叉
> （架构、数据模型、功能集都不同），比对只剩噪音。上游仓库保留作参照，闸门继续
> 核对它未被本模组污染，但不再做内容比对。

## 只读审计（闸门第 3 节，`-SkipAudits` 可跳过）

| 脚本 | 验什么 |
|---|---|
| `tools\harmony-shape-audit.ps1` | 补丁方法签名形状：Prefix 返回 void/bool、Finalizer 返回 Exception、Transpiler 返回 `IEnumerable<CodeInstruction>` |
| `tools\instance-injection-audit.ps1` | `__instance` 只对实例方法（同上 InstanceInjectionCheck，另给非 C# 视角） |
| `tools\patch-sig-audit.ps1` | 普通形参与目标方法同名同型 |
| `tools\result-injection-audit.ps1` | `__result` 的目标都有返回类型（void 上配 `__result` 拿不到值） |
| `tools\deadcode-audit.ps1` | 零调用点的静态方法，按「设计使然 / Spectre 有调用方但按裁决不补 / 自己造成的」三分，**退出码只对第三类敏感** |
| `tools\settings-ui-audit.ps1` | 每个设置项是否有界面入口（报告型，恒 exit 0） |

审计脚本在闸门里**只印一行摘要**，出问题才贴详情（`deadcode-audit` 是报告型脚本，
一次吐上百行，全量贴会把真正的 ✗ 埋掉）。

## 手动真跑脚本（不在闸门里，需手动跑）

改编码/转换参数后跑，它们调**游戏自带那个真 ffmpeg.exe**：

| 脚本 | 验什么 |
|---|---|
| `tools\ffmpeg-probe.ps1` | 编码参数四容器端到端：真 BGRA → 滤镜链 → 中间文件 → mux，产物音画双流俱在；奇数宽高 33×25 跑通 `pad`（裸 `vflip` 会 exit -542398533、产物 0 字节） |
| `tools\convert-probe.ps1` | 转换参数四容器；**目标为 mov 时视频必须重编码**——mov 复用 movenc 且拒绝 VP9 |
| `tools\deadcode-audit.ps1` | 零调用点的静态方法（同时也在闸门第 3 节跑，报告型） |

## 验不了的

只有一件，而且只有这一件：

**回放与视频渲染的最终效果只能在游戏里实跑确认。** 闸门守的是"不会静默废掉
整条链路"那类规则（用真 ffmpeg 跑参数、用真程序集反射核对补丁目标），
不是"画面好看"。离线全绿之后仍要按 `AGENT.md` §12 的实机路径跑一遍。

## 改完记得跑

预览器绿灯**不等于**主项目能编译：预览器是 `net10.0-windows`，只链接
`Ui\Loc.cs`、`Features\Replay` 下若干纯逻辑文件与检查自身；主项目是
`netstandard2.1`。两者编译的不是同一份源码集。
`verify.ps1` 把两边绑在一起就是为了这个。

另外两个会反复咬人的工具坑（改之前必读）：

1. `edit` 工具写 `tools\*.ps1` 会剥掉 UTF-8 BOM，PowerShell 5.1 按 ANSI 读会
   中文乱码报 ParserError，且常伪装成"被测对象全有问题"（脚本读到 0 字节）。
2. 注释剥离器（`HardTextCheck.StripComments` 与 `SrcUtil.Clean`）必须让
   `inStr` 优先于 `inChar`——字符串内的撇号（如 `"the game's"`）若切了
   `inChar`，其后整个文件的注释都剥不掉。
