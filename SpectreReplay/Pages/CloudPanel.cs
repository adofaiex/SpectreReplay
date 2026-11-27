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
    /// Steam 云：云端状态、配置备份的上传与取回。
    /// 错误代号一律翻成人话（CloudErrorText / CloudCfgErrorText），不要丢给玩家。
    /// </summary>
    internal static partial class Pages
    {

        // ── Steam 云 ────────────────────────────────────────────

        /// <summary>
        /// 把云端返回的内部代号翻成给人看的一句话。
        ///
        /// <para>云层的出参是错误**代号**（<c>not_available</c>、<c>decode_failed</c>…），
        /// 原本直接塞进提示里，玩家看到的是「云端操作失败：decode_failed」——既看不懂，
        /// 也把内部实现暴露在界面上。这里只做三分法：用不了 / 读不出来 / 云端没了。
        /// 认不出的代号原样返回，至少不会被吞成空字符串。</para>
        /// </summary>
        private static string CloudErrorText(string token)
        {
            switch (token)
            {
                case "cloud_off":
                case "not_available":
                case "no_dir":
                    return Loc.T("playback.cloud.err.unavailable");
                case "bad_name":
                case "decode_failed":
                case "load_failed":
                    return Loc.T("playback.cloud.err.unreadable");
                case "not_found":
                    return Loc.T("playback.cloud.err.notfound");
                default:
                    return token ?? "";
            }
        }

        /// <summary>云端按钮是否可点：同步开着 + Steam 接口可用。
        /// **不再要求 ReplayOn**：云端管的是已经存在的文件，关掉录制也应当能找回。</summary>
        private static bool CloudReady => CloudSyncOn && SteamCloud.Ready;

        /// <summary>一行云端状态（数字走缓存，见 CloudReplay.GetStats）。</summary>
        private static void DrawCloudStatus()
        {
            var s = CloudReplay.GetStats();
            string text;
            if (!SteamCloud.Ready)
            {
                // 非 Steam 版 / Steam 没运行：说清楚"这块功能为什么灰着"，
                // 否则玩家只会觉得开关拨了没用。
                text = Loc.T("playback.cloud.status.unavailable");
            }
            else if (!s.AppEnabled)
            {
                // 客户端里这个游戏的云同步被关掉：写入会"成功"但永不上传。
                text = Loc.T("playback.cloud.status.disabled");
            }
            else if (s.Replays == 0)
            {
                text = Loc.T("playback.cloud.status.empty");
            }
            else
            {
                text = Loc.F("playback.cloud.status.ok", s.Replays, SizeText(s.Bytes));
            }

            // 软阈值：只提醒，不自动删。云端配额是整个 app 共用的（含游戏自己的
            // 存档），堆到接近上限时 Steam 的新写入会失败，用户需要知道该清了。
            if (SteamCloud.Ready && s.AppEnabled
                && (s.Files > CloudReplay.SoftCount || s.Bytes > CloudReplay.SoftBytes))
            {
                text += "\n" + Loc.F("playback.cloud.status.heavy", s.Files, SizeText(s.Bytes));
            }
            OnGui.NoteText(text);
        }

        /// <summary>
        /// 配置文件的云端那一小块：一行状态 + 手动上传 / 手动取回。
        ///
        /// <para><b>为什么不并进上面两颗批量按钮。</b>批量管的是**回放文件**，
        /// 而配置文件（Configs.json）只有一份。把它混进「全部上传」，玩家点那颗
        /// 按钮时会顺带改掉云端的配置，而它的说明里一个字都没提；反过来，
        /// 取回是**覆盖本地**的动作，跟「回填缺失的回放」（只加不减）性质完全相反，
        /// 混在一起迟早有人点错。</para>
        ///
        /// <para><b>取回必须两段确认。</b>它覆盖掉的是玩家刚调好的那一整套设置。
        /// 备份（.bak）是兜底、不是免确认的理由：真到要靠备份的时候，玩家已经
        /// 经历过一次「我的设置怎么全变了」。确认态与删除那颗各自独立，
        /// 理由见 <see cref="_pendingConfigRestore"/>。</para>
        /// </summary>
        private static void DrawCloudConfigBlock()
        {
            GUILayout.Space(6f);
            GUILayout.Label(Loc.T("playback.cloud.cfg.title"), Gui.LabelStyle);

            // 状态行。取不到就**明说没有**，不要退化成「0 B」——那读起来像
            // 「云端有一份空备份」，而空配置正好会让取回把本地清空。
            if (TryGetConfigBackupInfoCached(out int size, out uint timestampUnix))
            {
                OnGui.NoteText(Loc.F("playback.cloud.cfg.status",
                    ConfigBackupTimeText(timestampUnix), SizeText(size)));
            }
            else
            {
                OnGui.NoteText(Loc.T("playback.cloud.cfg.none"));
            }

            GUILayout.BeginHorizontal();
            DrawCloudCfgUploadButton();
            DrawCloudCfgRestoreButton();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            OnGui.Note("playback.cloud.cfg.note");
        }

        /// <summary>
        /// 云端配置备份的元信息（带一秒缓存，见 <see cref="_cfgInfoTick"/>）。
        /// 底层失败一律当作「没有备份」——状态行说「云端还没有配置备份」比
        /// 抛异常或显示半截数字都更接近玩家能处理的事实。
        /// </summary>
        private static bool TryGetConfigBackupInfoCached(out int size, out uint timestampUnix)
        {
            if (_cfgInfoTick == int.MinValue
                || (uint)(Environment.TickCount - _cfgInfoTick) >= 1000u)
            {
                _cfgInfoHas = CloudReplay.TryGetConfigBackupInfo(
                    out _cfgInfoSize, out _cfgInfoTimestamp);
                _cfgInfoTick = Environment.TickCount;
            }
            size = _cfgInfoSize;
            timestampUnix = _cfgInfoTimestamp;
            return _cfgInfoHas;
        }

        /// <summary>手动上传/取回之后立刻让状态行重算，别让玩家盯着旧时间戳。</summary>
        private static void InvalidateConfigBackupInfo()
        {
            _cfgInfoTick = int.MinValue;
        }

        /// <summary>
        /// Unix 秒 → 本地时间（精确到分钟）。
        ///
        /// 云端给的是 **UTC 的 Unix 秒**，直接当本地时间画会差好几个时区——
        /// 而备份时间恰恰是玩家用来判断「这是不是我最新那份」的唯一线索，
        /// 差八小时会让人以为备份是旧的、于是又覆盖一次。
        /// </summary>
        private static string ConfigBackupTimeText(uint timestampUnix)
        {
            if (timestampUnix == 0)
            {
                return Loc.T("playback.cloud.cfg.unknown");
            }
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(timestampUnix).ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                // FromUnixTimeSeconds 对 long 超出范围才抛；uint 进不来，
                // 但宁可显示「时间未知」也不要让 OnGUI 抛出去（UMM 会收掉整块面板）。
                return Loc.T("playback.cloud.cfg.unknown");
            }
        }

        /// <summary>
        /// 「上传配置到云端」。一次小文件写入（几十 KB 以内），与单份回放上传
        /// 同量级，所以同步执行；批量上传/回填那种几十次读写才需要推迟到下一拍。
        /// </summary>
        private static void DrawCloudCfgUploadButton()
        {
            // 宽度按文案实测：这两颗是全页最长的英文文案（"Upload Config to
            // Cloud" / "Restore Config from Cloud"），写死 150f 直接被裁成
            // "Jpload Conf to Clouc"（2026-10-27 用户截图）。西语/俄语更长。
            string label = Loc.T("playback.cloud.cfg.upload");
            Rect r = Gui.ButtonRect(label, Gui.ButtonStyle, 150f, 28f);
            Gui.Tip(r, "playback.cloud.cfg.upload.tip");
            bool hit = GUI.Button(r, label, Gui.ButtonStyle);
            if (!CloudReady || Gui.IsDim)
            {
                hit = false;
            }
            if (!hit)
            {
                return;
            }
            // 点了上位另一颗按钮，之前那个待确认的取回就作废：否则玩家隔了一会儿
            // 再点一下取回，会直接进第二段把设置覆盖掉——而他已经忘了第一下。
            _pendingConfigRestore = "";
            UploadConfigToCloud();
        }

        private static void UploadConfigToCloud()
        {
            try
            {
                if (!CloudReplay.UploadConfigManual(out string err))
                {
                    Debug.LogWarning("[SpectreReplay] 手动上传配置失败（" + err + "）");
                    Status.Warn("playback.cloud.failed", CloudCfgErrorText(err, false));
                    return;
                }
                InvalidateConfigBackupInfo();
                Status.Say("playback.cloud.cfg.uploaded");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 手动上传配置失败：" + ex.Message);
                Status.Warn("playback.cloud.failed", Loc.T("playback.cloud.cfg.err.other"));
            }
        }

        /// <summary>
        /// 「从云端取回配置」（两段确认）。第一下只把按钮变成确认态，
        /// 本地配置文件一个字节都不动。
        /// </summary>
        private static void DrawCloudCfgRestoreButton()
        {
            bool pending = _pendingConfigRestore.Length != 0;
            string label = Loc.T(pending
                ? "playback.cloud.cfg.confirm"
                : "playback.cloud.cfg.restore");
            GUIStyle style = pending ? Gui.AccentButtonStyle : Gui.ButtonStyle;
            // 同上传那颗：宽度按实测文案算，确认态文案短的才回落到 110f。
            Rect r = Gui.ButtonRect(label, style, pending ? 110f : 150f, 28f);
            Gui.Tip(r, "playback.cloud.cfg.restore.tip");
            bool hit = GUI.Button(r, label, style);
            if (!CloudReady || Gui.IsDim)
            {
                hit = false;
            }
            if (!hit)
            {
                return;
            }
            if (!pending)
            {
                _pendingConfigRestore = "config";   // 第一下只进确认态，本地一个字节都不动
                return;
            }
            _pendingConfigRestore = "";
            RestoreConfigFromCloud();
        }

        private static void RestoreConfigFromCloud()
        {
            try
            {
                if (!CloudReplay.RestoreConfigManual(out string err))
                {
                    Debug.LogWarning("[SpectreReplay] 手动取回配置失败（" + err + "）");
                    Status.Warn("playback.cloud.failed", CloudCfgErrorText(err, true));
                    return;
                }
                InvalidateConfigBackupInfo();
                // 成功提示里必须带 .bak：取回**覆盖了本地配置**，玩家得知道
                // 原来那份还在、叫什么名字，否则"设置全变了"没有任何退路可循。
                Status.Say("playback.cloud.cfg.restored");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 手动取回配置失败：" + ex.Message);
                Status.Warn("playback.cloud.failed", Loc.T("playback.cloud.cfg.err.other"));
            }
        }

        /// <summary>
        /// 手动上传/取回配置的错误代号 → 给人看的一句话。
        ///
        /// <para>**绝不把代号透给玩家。** 与 <see cref="CloudErrorText"/> 同一个立场，
        /// 但这里不能复用它：那个的 default 分支原样返回代号（它的代号集合是
        /// 回放那一套），而配置这块的代号里有 <c>no_config_path</c> 这种内部
        /// 实现词，露出来既看不懂也说明不了该怎么办。所以默认分支落到一句
        /// 「细节见日志」的人话上。</para>
        ///
        /// <para><paramref name="restoring"/> 只影响 <c>write_failed</c>：上传时
        /// 它是「云端写不进去」，取回时它是「本地文件写不进去」，两者该给的
        /// 建议完全不同（等配额 vs 关掉占用它的程序）。</para>
        /// </summary>
        private static string CloudCfgErrorText(string token, bool restoring)
        {
            switch (token)
            {
                case "cloud_off":
                case "not_available":
                    return Loc.T("playback.cloud.err.unavailable");
                case "no_config":
                case "missing_local":
                    return Loc.T("playback.cloud.cfg.err.noconfig");
                case "empty_config":
                    return Loc.T("playback.cloud.cfg.err.empty");
                case "read_failed":
                    return Loc.T("playback.cloud.cfg.err.read");
                case "no_config_path":
                    return Loc.T("playback.cloud.cfg.err.path");
                case "not_found":
                    return Loc.T("playback.cloud.cfg.err.nocloud");
                case "download_failed":
                    return Loc.T("playback.cloud.cfg.err.download");
                case "write_failed":
                    return Loc.T(restoring
                        ? "playback.cloud.cfg.err.write"
                        : "playback.cloud.cfg.err.upload");
                case "upload_failed":
                case "too_large":
                    return Loc.T("playback.cloud.cfg.err.upload");
                default:
                    return Loc.T("playback.cloud.cfg.err.other");
            }
        }

        /// <summary>
        /// 点按钮后把云端读写在**下一拍**执行。
        ///
        /// 与目录选择框同理：批量上传/回填是几十次文件读写 + P/Invoke，
        /// 直接写在 OnGUI 的 MouseUp 趟里会让这一帧拖长（界面卡一下）。
        /// 放到主线程回调里，画面先响应，结果再用通知条汇报。
        /// </summary>
        private static void QueueCloudAction(bool restore)
        {
            try
            {
                MainThread.Defer(() =>
                {
                    try
                    {
                        if (restore)
                        {
                            var r = CloudReplay.RestoreMissing();
                            if (r.Restored > 0 && r.Failed > 0)
                            {
                                Status.Warn("playback.cloud.partial", Loc.F("playback.cloud.partial.detail",
                                    r.Restored, r.Failed, CloudErrorText(r.LastError)));
                            }
                            else if (r.Restored > 0)
                            {
                                Status.Say("playback.cloud.restored", r.Restored.ToString());
                            }
                            else if (r.Failed > 0)
                            {
                                Status.Warn("playback.cloud.failed", CloudErrorText(r.LastError));
                            }
                            else
                            {
                                Status.Say("playback.cloud.none");
                            }
                        }
                        else
                        {
                            var r = CloudReplay.UploadAllLocal();
                            if (r.Uploaded > 0 && r.Failed > 0)
                            {
                                Status.Warn("playback.cloud.partial", Loc.F("playback.cloud.partial.detail",
                                    r.Uploaded, r.Failed, CloudErrorText(r.LastError)));
                            }
                            else if (r.Uploaded > 0)
                            {
                                Status.Say("playback.cloud.uploaded", r.Uploaded.ToString());
                            }
                            else if (r.Failed > 0)
                            {
                                Status.Warn("playback.cloud.failed", CloudErrorText(r.LastError));
                            }
                            else
                            {
                                Status.Say("playback.cloud.uptodate");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[SpectreReplay] 云端操作失败：" + ex.Message);
                        Status.Warn("playback.cloud.failed", ex.GetType().Name);
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 无法排队云端操作：" + ex.Message);
            }
        }
    }
}
