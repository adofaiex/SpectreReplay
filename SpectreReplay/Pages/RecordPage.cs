using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpectreReplay.Features.Cloud;
using SpectreReplay.Features.EffectRemover;
using SpectreReplay.Features.Replay;
using SpectreReplay.Features.VideoRender;
using SpectreReplay.Ui;
using UnityFileDialog;
using UnityEngine;
using static SpectreReplay.Settings;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Pages
{
    /// <summary>
    /// 录制页：录制开关与保存策略、热键、特殊按键、键音、键位映射、保存目录。
    /// 只画控件与接线；落盘一律走 Pages.Commit（拖动中推迟写盘）。
    /// </summary>
    internal static partial class Pages
    {
        // ── 录制 ────────────────────────────────────────────────
        internal static void DrawRecord()
        {
            Gui.Section("page.record", Gui.Accent, () =>
            {
                // 独立窗口把总开关移到最上方；UMM 旧入口保留原顺序。
                if (!Gui.ModernMode) OnGui.Note("record.howto");
                if (!Gui.ModernMode) Gui.Divider();
                OnGui.Row("record.recording", ReplayOn,
                    v =>
                    {
                        ReplayOn = v;
                        if (!v)
                        {
                            if (VideoRender.CaptureInProgress)
                                VideoRender.CancelRender();
                            PlayActions.StopPlaying();
                            PlayActions.StopRecording();
                        }
                        SpectreState.SetGetKeyAsyncEnabled(v && GetDataFromAsyncInput);
                        Commit();
                    });
                OnGui.Row("record.autosave", AutoSave, v => { AutoSave = v; Commit(); }, ReplayOn);
                // 保存策略七项：**每一项都真的在控制回放什么时候落盘**
                // （Patch_Recording.cs:136/137/139/142/163、SpectreState.cs:254、
                // ReplayRecorder.cs:66、Patch_Lifecycle.cs:92/261），而原版界面上
                // 一个都改不了——配置文件里那七项只可能是默认值。玩家最常见的两个诉求
                // 正好都被默认值挡着：想存"打了一半的练习"（CompleteSave 默认
                // true ⇒ 非完整通关直接不存）、想存"失手了的一局"
                // （DontSaveWhenMiss 默认 true ⇒ 有 miss 直接不存）。
                OnGui.Row("record.completesave", CompleteSave, v => { CompleteSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.dontsavewhenauto", DontSaveWhenAuto, v => { DontSaveWhenAuto = v; Commit(); }, ReplayOn);
                OnGui.Row("record.dontsavewhenmiss", DontSaveWhenMiss, v => { DontSaveWhenMiss = v; Commit(); }, ReplayOn);
                OnGui.Row("record.latesave", LateSave, v => { LateSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.failsave", FailSave, v => { FailSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.backup", Backup, v => { Backup = v; Commit(); }, ReplayOn);
                OnGui.Row("record.manualsave", ManualSave, v => { ManualSave = v; Commit(); }, ReplayOn);
                OnGui.Row("record.cancelsave", Settings.CancelSave,
                    v => { Settings.CancelSave = v; Commit(); }, ReplayOn);
                // 异步输入：**开关前先看游戏侧有没有开。** 这条设置只在游戏的
                // "使用异步输入"打开时才有意义——`AsyncKeyRecorder.FlushFrame`
                // 里那个 `AsyncInputManager.isActive` 守卫就是它：游戏侧没开时
                // 我们即使勾了也一个按键都取不到，而界面上完全看不出原因，
                // 玩家只会觉得"勾了没效果"。
                // 所以这里把游戏侧的状态直接说出来，没开就明确告诉他去哪儿开。
                DrawAsyncInputRow();
                // 镜像到系统：与上一条相邻，因为两者都是「回放时怎么把按键交出去」的
                // 口味选项——一个决定游戏读什么，一个决定别的软件看不看得见。
                OnGui.Row("replay.mirror", MirrorInputToOS,
                    v => { MirrorInputToOS = v; Commit(); }, ReplayOn);
                // （原「界面语言跟随游戏」开关已删除：面板语言由 Ui.Loc.RefreshLanguage
                //   始终跟随游戏，那个开关实际只影响 14 语言大表里的通知文案，
                //   默认 true 就是对的，留着只会让人以为界面语言能单独设。）
                Gui.Divider();
                DrawSaveDirRow();
            });

            Gui.Section("record.hotkeys", Gui.Accent, () =>
            {
                // 按任意键改键：等待期间 Esc 取消并独占 Esc（见 OnGui.RowHotkey）。
                // 独立设置窗口的开关键也在这里：它不是 UMM 的固定入口，必须
                // 可改，否则会和 Insert 系按键/其他模组冲突。
                OnGui.RowHotkey("ui.windowkey", () => Settings.WindowButton,
                    k => { Settings.WindowButton = k; Commit(); });
                // 这两个键是**手动存这一局**的入口——没有它，练到一半的短局
                // 只能等通关自动保存，等于存不下来。
                //
                // 各自跟着上面那两项开关压暗：ManualSave / CancelSave 关掉时，
                // 对应的补丁整个不挂，这两个键按下去不会有任何反应。留着可点的
                // 改键行，玩家会以为是自己绑错了键。压暗后 RowHotkey 内部同时
                // 拦掉点击与扫键（Gui.Dim 只染色，拦不住内建 GUI.Button）。
                OnGui.RowHotkey("record.savekey", () => SaveButton, k => { SaveButton = k; Commit(); },
                    ReplayOn && ManualSave);
                OnGui.RowHotkey("record.cancelsavekey", () => CancelSaveButton, k => { CancelSaveButton = k; Commit(); },
                    ReplayOn && Settings.CancelSave);
                OnGui.Note("record.note");
            });

            // 键盘音三项必须有界面：MicrophoneOffset 是毫秒级校准值，键盘声整体
            // 偏早或偏晚时它是唯一的修正手段，不能让玩家去手改 Configs.json。
            Gui.Section("record.keybd", Gui.Accent, () =>
            {
                OnGui.Row("record.keybd.record", KeybdSoundRecordActive,
                    v => { KeybdSoundRecordActive = v; Commit(); });
                // 音量与偏移**只在录音开着时才有意义**：消费点全在
                // `if (Settings.KeybdSoundRecordActive …)` 里面
                // （Patch_Playback.cs:65/93、Patch_Lifecycle.cs:52）。
                // 录音关掉时调它们不会产生任何效果。
                // 音量：**内部量纲是 0..10（10 = 满）**，Patch_Playback 用它 /10f
                // 当 AudioSource.volume（Patch_Playback.cs:253 还会再 Clamp 到 0..10）。
                // 滑条原先写的是 0..100 且显示 "xx %"：值域比真实量纲宽十倍，于是
                // 拖到 10 就已经满音量，剩下 90 的行程完全无效——玩家只会觉得
                // "音量键坏了"。改成 0..10、显示时乘 10 换成百分比，行程就与
                // 实际音量一一对应；配置里已存的 10 仍然表示满音量，听感不变。
                OnGui.RowSlider("record.keybd.volume", Mathf.Clamp(KeybdSoundVolume, 0, 10) * 10f, 0f, 100f,
                    v => ((int)v) + " %",
                    v => { KeybdSoundVolume = Mathf.Clamp((int)Mathf.Round(v / 10f), 0, 10); Commit(); },
                    KeybdSoundRecordActive);
                // 偏移：毫秒，可正可负（PSlider 支持负区间）
                OnGui.RowSlider("record.keybd.offset", MicrophoneOffset, -500f, 500f,
                    v => ((int)v) + " ms", v => { MicrophoneOffset = (int)v; Commit(); },
                    KeybdSoundRecordActive);
                // 嵌入档位：只影响保存那一刻写进回放本体的音频段（见 ReplayManager）。
                // 与上面两项不同，它只在**下次保存**时生效，已经存好的回放不会重压。
                OnGui.Row("record.keybd.compact", KeybdSoundCompact,
                    v => { KeybdSoundCompact = v; Commit(); }, KeybdSoundRecordActive);
                OnGui.Note("record.keybd.note");
                // 录键音的质量告诫（2026-11-07 用户要求）：键音是实时合成/采样的，
                // 效果未必干净；黄色与普通说明区分，警告意味一眼可辨。
                OnGui.Note("record.keybd.warn", Gui.WarnCol);
            });

            // ── 特殊按键 ────────────────────────────────────────
            //
            // 游戏把 F1~F12 / Tab / End / Alt / Win / Print 等当作"不算数"的按键，
            // 按下也不计入按键数，因此**不会进回放**。对普通游玩这是合理的
            // （多半是误触或截图），但对录制来说假设是错的：玩家有意用这些键
            // 打谱时，回放里就是缺的，而界面上完全看不出为什么。
            //
            // 候选集不是随手写的。键盘那 22 个来自
            // `RDInputType_Keyboard.SpecialKeys` 的静态初始化数据（反编译读出来的
            // 实际值：Print/SysReq/左右 Alt/左右 Win/左右 Meta/Tab/End/F1~F12），
            // 异步那 4 个来自 `RDInputType_AsyncKeyboard.SpecialKeys`
            // （PrintScreen/F12/LAlt/Super）。**候选集必须跟着游戏的实际数组走**，
            // 否则玩家会看到一堆勾了也没用的键。
            Gui.Section("record.specialkeys", Gui.Accent, () =>
            {
                OnGui.Note("record.specialkeys.note");
                // 分组标签用 KeyValue 而不是 Row：这两行是**标题**不是控件，
                // 用 Row 会多画一颗开关，而玩家会去点它（真正的开关是下面的格子）。
                Gui.KeyValue("record.specialkeys.kb", "", Gui.DimCol);
                // **整块挂录制总开关**：录制关掉时 `SetFeatureToggle(SpecialKeys)`
                // 的谓词里 `Settings.ReplayOn` 为假 ⇒ 两条补丁整个不挂载，
                // 勾了也不会有任何效果。灰掉比"能点但不生效"诚实。
                Gui.KeyChips(KeyboardSpecialKeyNames, AllowedKeyboardSpecialKeys, 6,
                    () => Commit(), ReplayOn, "record.specialkeys.kb");
                if (GameAsyncInputOn())
                {
                    Gui.KeyValue("record.specialkeys.async", "", Gui.DimCol);
                    Gui.KeyChips(AsyncSpecialKeyNames, AllowedAsyncSpecialKeys, 4,
                        () => Commit(), ReplayOn, "record.specialkeys.async");
                }
                else
                {
                    OnGui.Note("record.specialkeys.async.off");
                }
            });

            Gui.Section("record.keymap", Gui.Accent, () =>
            {
                OnGui.Row("record.keymap.on", KeyConvertEnabled, v =>
                {
                    KeyConvertEnabled = v;
                    Commit();
                    // 建表是**启动时**做的（Settings.check），关掉/打开后必须
                    // 立刻重建，否则开关看着变了、实际映射还是旧的。
                    Settings.BuildKeyCodeConvertTable();
                });
                if (!KeyConvertEnabled)
                {
                    OnGui.Note("record.keymap.off");
                    return;
                }
                // 「只模拟列表中的按键」——与总开关同样要**当场重建表**。
                // 它改的是「表外那些键要不要静默」：开启后表外的键在回放时
                // 全部跳过（key_code_convert 填 0），只用手柄/宏键盘几个键
                // 打谱时靠它屏蔽其余按键。
                OnGui.Row("record.keymap.limit", LimitKey, v =>
                {
                    LimitKey = v;
                    Settings.BuildKeyCodeConvertTable();   // 不重建就当场不生效
                    Commit();
                });
                OnGui.Note("record.keymap.note");
                DrawKeyRemapList();
            });
        }

        // 正在等按键的那一行（-1 = 没有）。_remapFrom 表示改左半还是右半。
        private static int _remapRow = -1;
        private static bool _remapFrom;
        private static int _saveDirPickVersion;

        internal static bool KeyRemapCapturing => _remapRow >= 0;

        internal static void CancelCaptures()
        {
            _remapRow = -1;
            _remapFrom = false;
            _pickSaveDirPending = false;
            _saveDirPickVersion++;
        }

        private static void DrawKeyRemapList()
        {
            var list = Settings.key_code_convert_list;
            // 槽位宽度按**当前所有可能出现的文案**实测取最大：绑定的键名
            // （VkName 会出「PrintScreen」这种长名）、「未绑定」、等待按键时的
            // 「按下任意键…」。写死 74 时长文案折行，两行塞进 24px 高的按钮
            // 就是拦腰裁断。整张表取同一个值：两列格子逐行各量各的会左右错位。
            float slotW = Gui.ButtonW(Loc.T("ui.presskey"), Gui.ButtonStyle, 74f);
            slotW = Mathf.Max(slotW,
                Gui.ButtonW(Loc.T("ui.keymap.none"), Gui.ButtonStyle, slotW));
            foreach (KeyValuePair<ushort, ushort> probe in list)
            {
                slotW = Mathf.Max(slotW, Gui.ButtonW(OnGui.VkName(probe.Key), Gui.ButtonStyle, slotW));
                slotW = Mathf.Max(slotW, Gui.ButtonW(OnGui.VkName(probe.Value), Gui.ButtonStyle, slotW));
            }
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i;
                KeyValuePair<ushort, ushort> pair = list[idx];
                bool capturing = _remapRow == idx;
                OnGui.RowKeyRemap(pair.Key, pair.Value, capturing, _remapFrom, slotW,
                    from =>
                    {
                        // 再点同一格 = 取消等待
                        bool same = _remapRow == idx && _remapFrom == from;
                        _remapRow = same ? -1 : idx;
                        _remapFrom = from;
                        if (!same) OnGui.PrimeKeyRemapCapture();
                    },
                    vk =>
                    {
                        if (_remapRow != idx) return;
                        if (vk == 0) { _remapRow = -1; return; }
                        KeyValuePair<ushort, ushort> cur = list[idx];
                        list[idx] = _remapFrom
                            ? new KeyValuePair<ushort, ushort>(vk, cur.Value)
                            : new KeyValuePair<ushort, ushort>(cur.Key, vk);
                        _remapRow = -1;
                        ApplyKeyRemap();
                    },
                    () =>
                    {
                        KeyValuePair<ushort, ushort> cur = list[idx];
                        list[idx] = new KeyValuePair<ushort, ushort>(cur.Key, 0);
                        _remapRow = -1;
                        ApplyKeyRemap();
                    },
                    () =>
                    {
                        list.RemoveAt(idx);
                        _remapRow = -1;
                        ApplyKeyRemap();
                    });
            }
            if (list.Count == 0)
            {
                OnGui.Note("record.keymap.empty");
            }
            OnGui.Button("record.keymap.add", () =>
            {
                Settings.key_code_convert_list.Add(new KeyValuePair<ushort, ushort>(0, 0));
                ApplyKeyRemap();
            }, false);
        }

        /// <summary>
        /// 把 list 序列化回 <see cref="Settings"/>.<see cref="KeyCodeConvert"/> 并重建运行时映射表。
        ///
        /// 对应 <c>Settings.KeyCodeConvert</c>（持久化 JSON 源）与 <c>Settings.key_code_convert</c>
        /// （运行时表）——`settings-ui-audit.ps1` 按字段名判界面入口，这里显式点名。
        ///
        /// **两步都不能省**：只序列化不建表 ⇒ 界面显示变了、实际映射还是旧的；
        /// 只建表不序列化 ⇒ 当场生效、下次启动全部丢失。
        /// 与上游 ApplyRemapChange 的顺序一致（先写 JSON 再 BuildKeyCodeConvertTable）。
        /// </summary>
        private static void ApplyKeyRemap()
        {
            KeyCodeConvert = Newtonsoft.Json.JsonConvert.SerializeObject(
                Settings.key_code_convert_list, Newtonsoft.Json.Formatting.Indented);
            Settings.BuildKeyCodeConvertTable();   // 这一步不能省：只写 JSON 不重建表，本次会话内映射仍是旧的
            Commit();
        }

        /// <summary>
        /// 键盘侧被游戏排除的特殊键。**顺序与分组照着游戏的 SpecialKeys 排**，
        /// 前后两组（功能键 / 修饰与系统键）在视觉上能连读。
        /// 名字必须是 <see cref="UnityEngine.KeyCode"/> 的真实成员——
        /// 存的是名字，解析失败会被静默跳过（见 Patch_SpecialKeys.EnsureCodes）。
        /// </summary>
        private static readonly string[] KeyboardSpecialKeyNames =
        {
            "Print", "SysReq", "Tab", "End",
            "LeftAlt", "RightAlt", "LeftWindows", "RightWindows",
            "LeftMeta", "RightMeta",
            "F1", "F2", "F3", "F4", "F5", "F6",
            "F7", "F8", "F9", "F10", "F11", "F12",
        };

        /// <summary>
        /// 异步侧被游戏排除的特殊键（SkyHook.KeyLabel 的成员名）。
        /// 游戏那边只有这 4 个，不要照"看起来像"往里加。
        /// </summary>
        private static readonly string[] AsyncSpecialKeyNames =
        {
            "PrintScreen", "F12", "LAlt", "Super",
        };

        /// <summary>游戏侧是否开了异步输入。取不到就当没开（宁可不显示，也不误导）。</summary>
        private static bool GameAsyncInputOn()
        {
            try
            {
                return RDC.useAsyncInput;
            }
            catch
            {
                return false;
            }
        }


        // ── 保存目录 ────────────────────────────────────────────

        /// <summary>点击后置位，等下一拍再弹选择框（见 DrawSaveDirRow）。</summary>
        private static bool _pickSaveDirPending;

        private static void DrawAsyncInputRow()
        {
            OnGui.Row("record.asyncinput", GetDataFromAsyncInput,
                v =>
                {
                    GetDataFromAsyncInput = v;
                    SpectreState.SetGetKeyAsyncEnabled(Settings.ReplayOn && v);
                    Commit();
                });

            bool gameOn;
            try
            {
                gameOn = RDC.useAsyncInput;
            }
            catch
            {
                // 游戏还没初始化：不下结论，也不显示可能误导的红字。
                return;
            }
            if (gameOn)
            {
                // 游戏侧已开 ⇒ 本开关是生效的，不再加噪音。
                return;
            }
            // 游戏侧没开：说明"现在不生效"以及去哪儿开。
            // **不替玩家改写设置值**——他可能只是临时在游戏里关了异步输入，
            // 我们不替他做决定，只把事实讲清楚。
            GUILayout.BeginHorizontal();
            GUILayout.Space(Gui.LabelW);
            GUILayout.Label(Loc.T("record.asyncinput.off"), Gui.NoteStyle,
                GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }
        private static void DrawSaveDirRow()
        {
            if (Gui.ModernMode)
            {
                Gui.BeginModernRow();
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical();
                GUILayout.Label(Loc.T("record.savedir"), Gui.ModernTitleStyle,
                    GUILayout.ExpandWidth(true));
                GUILayout.Label(BreakPathForDisplay(SaveDirText()), Gui.ModernDescStyle,
                    GUILayout.ExpandWidth(true));
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                string dirLabel = Loc.T("record.changedir");
                if (GUILayout.Button(dirLabel, Gui.ButtonStyle,
                        GUILayout.Width(Gui.ButtonW(dirLabel, Gui.ButtonStyle, 90f)),
                        GUILayout.Height(28f)))
                {
                    if (is_recording) Status.Warn("record.savedir.busy");
                    else _pickSaveDirPending = true;
                }
                GUILayout.EndHorizontal();
                Gui.EndModernRow();
                DrawPickSaveDirDeferred();
                return;
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("record.savedir"), Gui.LabelStyle, GUILayout.Width(Gui.LabelW));
            // 路径可能很长（换到别的盘之后尤其），中段省略：盘符/开头与目录名/
            // 结尾是玩家用来确认"是不是我选的那个"的两端，尾巴省略反而认不出。
            GUILayout.Label(BreakPathForDisplay(SaveDirText()), Gui.LabelStyle,
                GUILayout.ExpandWidth(true));
            string legacyDirLabel = Loc.T("record.changedir");
            if (GUILayout.Button(legacyDirLabel, Gui.ButtonStyle,
                    GUILayout.Width(Gui.ButtonW(legacyDirLabel, Gui.ButtonStyle, 90f)),
                    GUILayout.Height(22f)))
            {
                // **录制中不许改。** 本局键盘音写在**旧**目录的 tmp.wav 里，而保存时
                // SpectreState.SaveFile 是按当前 SavePath 去找它的
                // （SpectreState.cs:266 与 :277）。中途改掉 ⇒ tmp.wav 找不着，
                // 回放照存、键盘音静默丢失，而且把一局的产物拆到两个目录里。
                if (is_recording)
                {
                    Status.Warn("record.savedir.busy");
                }
                else
                {
                    _pickSaveDirPending = true;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            DrawPickSaveDirDeferred();
        }

        private static void DrawPickSaveDirDeferred()
        {
            if (!_pickSaveDirPending)
            {
                return;
            }
            _pickSaveDirPending = false;
            try
            {
                int version = _saveDirPickVersion;
                MainThread.Defer(() => PickSaveDirDeferred(version));
            }
            catch (Exception ex)
            {
                // 连"排进下一拍"都失败（极端情况下程序集没解析到）也不该让
                // OnGUI 抛出去——UMM 对 OnGUI 异常的处理是收掉整个设置面板。
                Debug.LogWarning("[SpectreReplay] 无法启动目录选择框：" + ex.Message);
            }
        }

        /// <summary>在 OnGUI 之外跑原生选择框，拿到结果再写设置。</summary>
        private static void PickSaveDirDeferred(int version)
        {
            if (version != _saveDirPickVersion) return;
            try
            {
                string dir = FileBrowser.PickFolder(
                    SaveDirText(), null, null, Loc.T("record.savedir.pick"));
                if (string.IsNullOrEmpty(dir) || version != _saveDirPickVersion)
                {
                    return;   // 玩家取消或配置恢复/切页已作废请求
                }
                SavePath = dir;
                // 走统一落盘入口：它除了写盘还会 Settings.check 并刷新补丁开关。
                SaveNow();
                // 列表缓存作废，下一帧从新目录重扫（否则「回放」页还列着旧目录的文件）
                _replayCache = null;
                _replayCacheTick = int.MinValue;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 选择保存目录失败：" + ex.Message);
            }
        }
    }
}
