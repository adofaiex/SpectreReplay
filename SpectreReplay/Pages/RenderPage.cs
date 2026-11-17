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
    /// 渲染页：渲染开关与状态、编码参数、开始 / 取消、格式转换、去特效预设。
    /// 参数块整块挂渲染总开关（见 DrawRender 里的 Gui.Dim）。
    /// </summary>
    internal static partial class Pages
    {
        // ── 视频渲染 ────────────────────────────────────────────
        internal static void DrawRender()
        {
            // 打开渲染页就后台预热编码器（Locate + NVENC + 文件缓存，内部
            // 30 秒节流）：spawn 一次 ffmpeg 要 ~5 秒，等用户点「开始渲染」
            // 时必须已经焐热，否则预启动的 Join 会吃掉开始后的头几秒。
            Features.VideoRender.VideoEncoder.WarmUp();
            Gui.Section("render.enable", Gui.Accent, () =>
            {
                // 绑 RenderOn（视频渲染开关）。不能用 ReplayOn：那是录制/回放总开关。
                OnGui.Row("render.on", RenderOn, v => { RenderOn = v; Commit(); });
                Gui.Divider();
                DrawRenderState();
            });

            if (RenderOn && !Features.VideoRender.VideoRender.Active)
            {
                Gui.Section("render.start", Gui.Accent, () =>
                {
                    OnGui.Button("render.start", StartRender, true);
                    OnGui.Note("render.note.offline");
                });
            }
            else if (Features.VideoRender.VideoRender.Active)
            {
                Gui.Section("render.cancel", Gui.WarnCol, () =>
                {
                    OnGui.Button("render.cancel",
                        () => Features.VideoRender.VideoRender.CancelRender(), false);
                });
            }

            // **渲染总开关关掉时，这一整块参数都压暗且不可调。**
            // RenderOn 为假时 VideoRender.StartRender 直接拒绝（VideoRender.cs:67），
            // 这些参数一个都不会被读到——留着可调等于让玩家白设一遍。
            // 用 Dim 包一整块而不是逐行加参数：这里二十多个控件，逐行写既啰嗦
            // 又容易漏一个，而漏的那一个正好会变成"整块都灰、只有它亮着"。
            Gui.Dim(!RenderOn, DrawRenderOptions);

            // 转换已有视频放在最后：它是"渲染完再做的事"，与渲染参数不是一类。
            DrawConvertSection();
        }

        private static void StartRender()
        {
            try
            {
                Features.VideoRender.VideoRender.StartRender();
            }
            catch (Exception ex)
            {
                // 这里绝不能让它抛出去：OnGUI 里抛异常，UMM 会把整个设置
                // 面板收掉，玩家看到的是"点一下窗口没了"，而不是一条错误。
                Debug.LogError("[SpectreReplay] 启动渲染失败：" + ex);
            }
        }

        /// <summary>
        /// 渲染状态：不在跑就说上一次的结果（或空闲），在跑就画阶段 + 进度条。
        ///
        /// 关键是**不在渲染时不能说"完成"**——那会让"从来没渲染过"和
        /// "刚渲染完一局"看起来一模一样。
        /// </summary>
        private static void DrawRenderState()
        {
            var s = Features.VideoRender.VideoRender.Current;
            if (!Features.VideoRender.VideoRender.Active)
            {
                string last;
                switch (s.Result)
                {
                    case Features.VideoRender.VideoRender.Outcome.Completed:
                        last = Loc.F("status.render_done", Path.GetFileName(s.Output));
                        break;
                    case Features.VideoRender.VideoRender.Outcome.Canceled:
                        last = Loc.T("status.render_canceled");
                        break;
                    case Features.VideoRender.VideoRender.Outcome.Failed:
                        last = string.IsNullOrEmpty(s.Error)
                            ? Loc.T("status.render_failed")
                            : s.Error;
                        break;
                    default:
                        last = Loc.T("status.idle");
                        break;
                }
                GUILayout.Label(last, Gui.NoteStyle, GUILayout.ExpandWidth(true));
                return;
            }

            string stage = string.IsNullOrEmpty(s.Stage) ? Loc.T("render.active") : s.Stage;
            GUILayout.Label(stage, Gui.LabelStyle);
            if (!string.IsNullOrEmpty(s.Detail))
                GUILayout.Label(s.Detail, Gui.NoteStyle);
            if (s.FramesTotal > 0)
            {
                Gui.ProgressBar(s.Progress,
                    s.FramesDone + " / " + s.FramesTotal + "   "
                    + (s.Progress * 100f).ToString("F0") + "%");
                if (s.EtaSeconds >= 0.0)
                {
                    GUILayout.Label(Loc.F("render.eta", FormatEta(s.EtaSeconds)), Gui.NoteStyle);
                }
            }
        }

        /// <summary>秒 → "1m30s" / "45s"。纯数字与 ASCII 单位，不进文案表。</summary>
        private static string FormatEta(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
            return ts.TotalMinutes >= 1
                ? (int)ts.TotalMinutes + "m" + ts.Seconds.ToString("00") + "s"
                : (int)ts.TotalSeconds + "s";
        }

        /// <summary>
        /// 渲染参数。**每个控件都真的接到一个 Settings 字段上。**
        ///
        /// 编码器 / 画质 / 结尾秒数在原版里 Settings 有字段、ConfigManager 也
        /// 落盘，却**没有任何一行界面能改它们**——配置文件里那几项只可能是默认值，
        /// 玩家改了没得反应，还以为是自己没找到。
        /// </summary>
        private static void DrawRenderOptions()
        {
            Gui.Section("render.options", Gui.Accent, () =>
            {
                if (RenderCaptureMode != 1)
                {
                    OnGui.RowSlider("render.width", RenderWidth, 320f, 7680f,
                        v => ((int)v) + " px", v => { RenderWidth = (int)v; Commit(); });
                    OnGui.RowSlider("render.height", RenderHeight, 240f, 4320f,
                        v => ((int)v) + " px", v => { RenderHeight = (int)v; Commit(); });
                }
                OnGui.RowSlider("render.fps", RenderFps, 15f, 480f,
                    v => ((int)v) + " fps", v => { RenderFps = (int)v; Commit(); });
                OnGui.RowSlider("render.tail", RenderTailSeconds, 0f, 15f,
                    v => v.ToString("0.#") + " s", v => { RenderTailSeconds = v; Commit(); });
                // 音画偏移：实机画面比音频滞后玩家校准量（扬声器延迟已被系统补偿），
                // 离线渲染没有那道补偿，校准会被原样烧进成片。默认跟随游戏校准复现
                // 实机观感；关闭后滑条给显式毫秒覆盖（0 = 完全对齐，不要实机滞后）。
                // 跟随态滑条灰显（值由游戏校准决定，拖动无意义），显示当前生效值 0。
                OnGui.Row("render.audio.offset.follow", RenderAudioOffsetMs < 0,
                    v => { if (v) RenderAudioOffsetMs = -1; else if (RenderAudioOffsetMs < 0) RenderAudioOffsetMs = 0; Commit(); });
                OnGui.RowSlider("render.audio.offset", Mathf.Max(0, RenderAudioOffsetMs), 0f, 500f,
                    v => ((int)v) + " ms", v => { RenderAudioOffsetMs = (int)v; Commit(); },
                    RenderAudioOffsetMs >= 0);
                OnGui.Row("render.stop.enable", RenderStopOn,
                    v => { RenderStopOn = v; Commit(); });
                OnGui.RowHotkey("render.stop.key", () => RenderStopKey,
                    k => { RenderStopKey = k; Commit(); }, RenderStopOn);
                if (RenderStopOn)
                    OnGui.Note("render.stop.hint");
                OnGui.RowDropdown("render.surface", new[]
                {
                    Loc.T("render.surface.clean"), Loc.T("render.surface.screen"),
                }, RenderCaptureMode, i => { RenderCaptureMode = i; Commit(); });
                OnGui.Note(RenderCaptureMode == 1
                    ? "render.surface.screen_note" : "render.surface.clean_note");
                OnGui.Row("render.cursor", RenderCursorOn,
                    v => { RenderCursorOn = v; Commit(); });
                OnGui.Row("render.10bit", Render10Bit,
                    v => { Render10Bit = v; Commit(); });
                if (RenderCaptureMode == 1 && Render10Bit)
                    OnGui.Note("render.10bit.screen_note");
                OnGui.Row("render.hdr10", RenderHdr10,
                    v => { RenderHdr10 = v; Commit(); });
                if (RenderHdr10 && (RenderVideoCodec != 1 || RenderCaptureMode != 0))
                    OnGui.Note("render.hdr10.note");

                // 组件类型排除：不手填 GameObject 名；空表 = 自动接管。
                // 用自绘 RowTextField（不是 GUILayout.TextField）：原生控件在 Gui 行栈里不落地。
                string overlayJoined = string.Join(", ",
                    RenderOverlayExcludeTypes ?? new List<string>());
                OnGui.RowTextField("render.overlay_exclude", overlayJoined, v =>
                {
                    RenderOverlayExcludeTypes = ParseOverlayTypes(v);
                    Commit();
                });
                GUILayout.Label(Loc.T("render.overlay_exclude.tip"), Gui.NoteStyle,
                    GUILayout.ExpandWidth(true));
                int overlayExcluded = Features.VideoRender.RenderOverlayCapture.ExcludedCount();
                if (overlayExcluded > 0)
                {
                    GUILayout.Label(Loc.F("render.overlay_excluded", overlayExcluded),
                        Gui.NoteStyle, GUILayout.ExpandWidth(true));
                }
            }, RenderOn);

            Gui.Section("render.codec", Gui.Accent, () =>
            {
                OnGui.RowDropdown("render.codec", new[]
                {
                    Loc.T("render.codec.auto"), Loc.T("render.codec.fastest"),
                    Loc.T("render.codec.quality"),
                }, RenderEncoderMode, i => { RenderEncoderMode = i; Commit(); });
                OnGui.RowDropdown("render.video_codec", new[] { "H264", "H265", "AV1" },
                    RenderVideoCodec, i => { RenderVideoCodec = i; Commit(); });
                OnGui.RowDropdown("render.hardware", new[]
                {
                    Loc.T("render.codec.auto"), "NVENC", "QSV", "AMF", Loc.T("render.codec.cpu"),
                }, RenderHardware, i => { RenderHardware = i; Commit(); });
                OnGui.RowDropdown("render.rate_control", new[] { "ABR", "CRF", "CQP", "CBR" },
                    RenderRateControl, i => { RenderRateControl = i; Commit(); });
                if (RenderRateControl == 1)
                {
                    OnGui.RowSlider("render.crf", RenderCrf, 0f, 51f,
                        v => ((int)v).ToString(), v => { RenderCrf = (int)v; Commit(); });
                }
                else if (RenderRateControl == 2)
                {
                    OnGui.RowSlider("render.cqp", RenderCqp, 0f, 51f,
                        v => ((int)v).ToString(), v => { RenderCqp = (int)v; Commit(); });
                }
                else
                {
                    OnGui.RowSlider("render.bitrate", RenderBitrateMbps, 0f, 160f,
                        v =>
                        {
                            int mbps = (int)v;
                            return mbps <= 0 ? Loc.T("render.codec.auto") : mbps + " Mbps";
                        },
                        v => { RenderBitrateMbps = (int)v; Commit(); });
                }
                OnGui.RowDropdown("render.preset", new[]
                {
                    Loc.T("render.codec.auto"),
                    "ultrafast", "superfast", "veryfast", "faster", "fast",
                    "medium", "slow", "slower", "veryslow", "placebo",
                }, RenderPreset, i => { RenderPreset = i; Commit(); });
                OnGui.RowDropdown("render.quality", new[]
                {
                    Loc.T("render.quality.low"), Loc.T("render.quality.standard"),
                    Loc.T("render.quality.high"), Loc.T("render.quality.extreme"),
                }, RenderQualityMode, i => { RenderQualityMode = i; Commit(); });
                OnGui.RowDropdown("render.format", new[] { "mp4", "mkv", "webm", "mov" },
                    RenderOutputFormat, i => { RenderOutputFormat = i; Commit(); });
                OnGui.RowInput("render.outdir");
                DrawEncoderNote();
            }, RenderOn);

        }

        /// <summary>
        /// 本局渲染实际会用的编码器。
        ///
        /// **为什么必须画出来。** <c>VideoEncoder.DescribeEncoder</c> 的结论
        /// 必须落到界面上：选「最快」时若 NVENC 探测失败，编码器会静默落到
        /// libx264(CPU)，渲染速度差一个数量级，而界面上看不到任何差别——
        /// 玩家只会觉得"这台机器跑不动"，去改画质/帧率那些无关的设置。
        /// 编码器那行复用已缓存的目录/探测结论；首次可能触发后台加载 + preflight，
        /// 但不会每帧起进程（WarmUp 会提前焐热）。
        /// ffmpeg 那行会**在首次且仅当模组目录没有 exe 时**探一次系统 PATH，
        /// 但结果带 30s 冷却缓存（VideoEncoder.Cooling），不会每帧拉进程。
        /// </summary>
        private static List<string> ParseOverlayTypes(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string line in text.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = line.Trim();
                if (s.Length > 0) list.Add(s);
            }
            return list;
        }

        private static void DrawEncoderNote()
        {
            try
            {
                var container = (VideoEncoder.OutContainer)Mathf.Clamp(RenderOutputFormat, 0, 3);
                string name = VideoEncoder.DescribeEncoder(
                    container,
                    (VideoEncoder.VideoCodec)Mathf.Clamp(RenderVideoCodec, 0, 2),
                    (VideoEncoder.HardwareBackend)Mathf.Clamp(RenderHardware, 0, 4),
                    Render10Bit, RenderHdr10);
                Gui.KeyValue("render.codec.current", name, Gui.DimCol);
                // 回退原因必须可见：玩家选了 AV1/MOV、或选了 QSV 但落到软件，
                // 只显示 libx264/libx265 会让人以为设置没生效。
                if (container == VideoEncoder.OutContainer.Mov && RenderVideoCodec == 2)
                    OnGui.Note("render.codec.mov_av1_fallback");
                if (RenderVideoCodec == 2 && RenderRateControl == 3 && name.Contains("libsvtav1", StringComparison.Ordinal))
                    OnGui.Note("render.codec.svt_cbr");
                if (RenderVideoCodec == 2 && name.Contains("libaom", StringComparison.Ordinal))
                    OnGui.Note("render.codec.aom_slow");
                if ((RenderHardware == 2 || RenderHardware == 3)
                    && !name.Contains(RenderHardware == 2 ? "qsv" : "amf", StringComparison.Ordinal))
                {
                    OnGui.Note(Loc.F("render.codec.hw_fallback",
                        RenderHardware == 2 ? "QSV" : "AMF"));
                }

                string exe = VideoEncoder.LocateEncoder();
                // 统一走能力校验（-version，缓存）：本地坏 ffmpeg.exe 不再被
                // File.Exists 误报成「就绪」。
                bool ready = VideoEncoder.VerifyEncoder(exe);
                Gui.KeyValue("render.ffmpeg",
                    Loc.T(ready ? "render.ffmpeg.ready" : "render.ffmpeg.missing"),
                    ready ? Gui.OkCol : Gui.BadCol);
            }
            catch (Exception ex)
            {
                // 纯提示信息：探测失败不该把整个设置页带走
                Debug.LogWarning("[SpectreReplay] 编码器提示失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 转换已有视频：VideoConverter 是纯逻辑转换器，**必须有界面入口**——
        /// 没有入口时"批量转格式"这个功能对玩家等于不存在。
        ///
        /// 这里给一个轻量入口：只认上面那个输出目录，目标容器就是 render.format
        /// 当前选的那个。不另开目录选择器——那是另一个功能，不是这个缺口的一部分。
        /// </summary>
        private static void DrawConvertSection()
        {
            Gui.Section("convert.title", Gui.Accent, () =>
            {
                OnGui.Note("convert.hint");
                Gui.Divider();
                OnGui.RowDropdown("convert.target", new[] { "mp4", "mkv", "webm", "mov" },
                    ConvertTarget, i => { ConvertTarget = i; Commit(); });

                var st = _convertState;
                if (st != null && st.Running)
                {
                    // 只读 snapshot：ConvertState 用 volatile 字段，Running=false 之后
                    // 才读 Messages（那边不是线程安全的）
                    GUILayout.Label(Loc.F("convert.running", st.Done, st.Total,
                        st.Converted, st.Skipped, st.Failed), Gui.NoteStyle,
                        GUILayout.ExpandWidth(true));
                    return;
                }

                if (st != null)
                {
                    // 上一次的结果。转换是在后台线程跑的，失败详情在 Messages 里
                    string summary = Loc.F("convert.done",
                        st.Converted, st.Skipped, st.Failed);
                    if (st.Error != null)
                        summary += "\n" + st.Error.Message;
                    else if (st.Messages.Count > 0)
                        summary += "\n" + string.Join("\n", st.Messages);
                    GUILayout.Label(summary, Gui.NoteStyle, GUILayout.ExpandWidth(true));
                    Gui.Divider();
                }

                OnGui.Button("convert.button", StartConvert, false);
            });
        }

        // 转换状态。null = 还没转过一次。只在主线程读写，后台线程只写它内部的
        // volatile 字段（ConvertState 的契约）
        private static VideoConverter.ConvertState _convertState;

        private static void StartConvert()
        {
            try
            {
                if (_convertState != null && _convertState.Running)
                    return;
                string dir = string.IsNullOrEmpty(RenderOutputDir)
                    ? Path.Combine(SavePath, "RenderVideos")
                    : RenderOutputDir;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Status.Warn("convert.err.no_dir");
                    return;
                }
                string ff = VideoEncoder.LocateEncoder();
                if (!VideoEncoder.VerifyEncoder(ff))
                {
                    Status.Warn("convert.err.no_ffmpeg");
                    return;
                }
                _convertState = VideoConverter.Start(dir,
                    (VideoEncoder.OutContainer)Mathf.Clamp(ConvertTarget, 0, 3), ff);
            }
            catch (Exception ex)
            {
                // OnGUI 里抛异常 = UMM 收掉整个设置面板
                Debug.LogError("[SpectreReplay] 启动转换失败：" + ex);
            }
        }

        // ── 去特效 ──────────────────────────────────────────────

        /// <summary>预设下拉框当前选中项对应的真实文件名；列表为空时返回 null。</summary>
        private static string SelectedPresetFile()
            => _presetSel >= 0 && _presetSel < _presetFiles.Count
                ? _presetFiles[_presetSel]
                : null;

        /// <summary>刷新预设列表缓存。保存/删除后 force=true 立即重扫。</summary>
        private static void RefreshPresets(bool force = false)
        {
            int now = Environment.TickCount;
            if (!force && _presetCacheTick != int.MinValue && now - _presetCacheTick < 2000)
                return;
            _presetCacheTick = now;
            _presetFiles.Clear();
            try
            {
                _presetFiles.AddRange(EffectPresets.List());
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 读取预设目录失败：" + ex.Message);
            }
            _presetOptions = _presetFiles.Select(PresetDisplayName).ToArray();
            if (_presetSel >= _presetOptions.Length)
                _presetSel = _presetOptions.Length - 1;
        }

        /// <summary>下拉框显示名 = 去掉 .json 后缀的真实文件名。</summary>
        private static string PresetDisplayName(string file)
            => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? file.Substring(0, file.Length - 5)
                : file;

        /// <summary>应用当前选中的预设：改开关、落盘并刷新补丁挂载（Commit 内做）。</summary>
        private static void ApplySelectedPreset()
        {
            string file = SelectedPresetFile();
            if (file == null)
            {
                Status.Say("effects.preset.none", null, Status.Level.Warning);
                return;
            }
            var r = EffectPresets.Apply(file);
            if (!r.Ok)
            {
                Status.Say("effects.preset.failed", r.Error, Status.Level.Warning);
                return;
            }
            Commit();
            Status.Say("effects.preset.applied", r.Applied.ToString());
        }

        /// <summary>把当前开关保存成预设，并让新预设处于选中态。</summary>
        private static void SaveCurrentPreset()
        {
            var r = EffectPresets.Save(_presetName.Text);
            if (!r.Ok)
            {
                Status.Say("effects.preset.failed", r.Error, Status.Level.Warning);
                return;
            }
            _presetName.Text = "";
            _presetName.Editing = false;
            RefreshPresets(force: true);
            int at = _presetFiles.IndexOf(r.FileName);
            if (at >= 0) _presetSel = at;
            Status.Say("effects.preset.saved", PresetDisplayName(r.FileName));
        }

        /// <summary>把选中的预设改名成名称框里的名字（不写 .json）。</summary>
        private static void RenameSelectedPreset()
        {
            string file = SelectedPresetFile();
            if (file == null)
            {
                Status.Say("effects.preset.none", null, Status.Level.Warning);
                return;
            }
            var r = EffectPresets.Rename(file, _presetName.Text);
            if (!r.Ok)
            {
                // empty name / exists 有各自的专门文案，其余走通用失败
                Status.Say(
                    r.Error == "empty name" ? "effects.preset.badname"
                    : r.Error == "exists" ? "effects.preset.exists"
                    : "effects.preset.failed",
                    r.Error, Status.Level.Warning);
                return;
            }
            string oldName = PresetDisplayName(file);
            RefreshPresets(force: true);
            int at = _presetFiles.IndexOf(r.FileName);
            if (at >= 0) _presetSel = at;
            _presetName.Text = "";
            _presetName.Editing = false;
            Status.Say("effects.preset.renamed", oldName + " → " + PresetDisplayName(r.FileName));
        }

        /// <summary>把选中的预设上传到 Steam 云（云端同名以本地为准）。</summary>
        private static void UploadSelectedPreset()
        {
            string file = SelectedPresetFile();
            if (file == null)
            {
                Status.Say("effects.preset.none", null, Status.Level.Warning);
                return;
            }
            var r = EffectPresets.UploadToCloud(file);
            if (!r.Ok)
            {
                Status.Say(r.Error == "not_available" ? "effects.preset.cloudoff" : "effects.preset.failed",
                    r.Error, Status.Level.Warning);
                return;
            }
            Status.Say("effects.preset.uploaded", PresetDisplayName(file));
        }

        /// <summary>从 Steam 云拉取本地没有的预设；本地已有的同名文件不覆盖。</summary>
        private static void PullCloudPresets()
        {
            var r = EffectPresets.PullFromCloud();
            if (r.Error != null)
            {
                Status.Say(r.Error == "not_available" ? "effects.preset.cloudoff" : "effects.preset.failed",
                    r.Error, Status.Level.Warning);
                return;
            }
            RefreshPresets(force: true);
            if (r.Pulled > 0)
                Status.Say("effects.preset.pulled", r.Pulled.ToString());
            else
                Status.Say("effects.preset.cloudfresh");
        }

        /// <summary>
        /// 删除按钮：两段确认，第一段只弹提醒（复用「确认删除」文案）。
        /// 确认态 5 秒有效，超时后要重新点两下。
        /// </summary>
        private static void DrawPresetDeleteButton()
        {
            string file = SelectedPresetFile();
            bool pending = file != null
                && string.Equals(file, _presetDeleteArm, StringComparison.OrdinalIgnoreCase)
                && Environment.TickCount - _presetDeleteArmTick < 5000;
            // 外观与点击协议走 ConfirmButton（与回放/云端删除共用一份）。
            // 这一处原本没有悬停说明，tipKey 传 null 保持原样——补一条说明是行为变化，
            // 不该混在重构里顺手做掉。
            if (!ConfirmButton.Draw(pending, "effects.preset.delete", "playback.delete.confirm",
                    null, 70f, 26f))
            {
                return;
            }
            if (!pending)
            {
                _presetDeleteArm = file;
                _presetDeleteArmTick = Environment.TickCount;
                Status.Say("effects.preset.confirm", null, Status.Level.Warning);
                return;
            }
            _presetDeleteArm = "";
            var d = EffectPresets.Delete(file);
            if (!d.Ok)
            {
                Status.Say("effects.preset.failed", d.Error, Status.Level.Warning);
                return;
            }
            RefreshPresets(force: true);
            Status.Say("effects.preset.deleted", PresetDisplayName(file));
        }

    }
}
