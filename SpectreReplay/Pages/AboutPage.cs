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
    /// 关于页：版本与更新检查、调试分区、保存目录展示与异步输入说明。
    /// </summary>
    internal static partial class Pages
    {

        // ── 关于 ────────────────────────────────────────────────
        internal static void DrawAbout()
        {
            Gui.Section("page.about", Gui.Accent, () =>
            {
                OnGui.Note("about.note");
                Gui.Divider();
                // 提示条停留时长。**放在「关于」页**：它是纯观感偏好，不掺录制/回放行为，
                // 而关于页正是"与玩法无关的那一档"。放在这一页最上面、信息行的前面：
                // 这一页其余内容全是只读信息，可交互的那一行不该夹在它们中间。
                // 每一项提示都是「这个值 + 自己的差量」（见 Settings.NotificationDuration），
                // 所以调大以后连错误提示也会跟着留久一点，不会出现"只有普通提示变长"的怪现象。
                OnGui.RowSlider("about.notif.seconds", NotificationSeconds, 1f, 10f,
                    v => v.ToString("0.#") + " s",
                    v => { NotificationSeconds = Mathf.Round(v * 10f) / 10f; Commit(); });
                // 提示卡出现位置：保留旧三项索引，再追加左上 / 右下 / 左下。
                OnGui.RowDropdown("about.notif.pos", new[]
                {
                    Loc.T("about.notif.pos.tr"),
                    Loc.T("about.notif.pos.tc"),
                    Loc.T("about.notif.pos.bc"),
                    Loc.T("about.notif.pos.tl"),
                    Loc.T("about.notif.pos.br"),
                    Loc.T("about.notif.pos.bl"),
                }, Settings.NotificationPos, v =>
                {
                    Settings.NotificationPos = v;
                    Ui.Toast.Pos = v;
                    Commit();
                });
                Gui.Divider();
                Gui.KeyValue("about.version", VersionText(), Gui.TextCol);
                Gui.KeyValue("about.engine", Loc.T("about.engine.value"), Gui.DimCol);
                Gui.KeyValue("about.path", BreakPathForDisplay(SaveDirText()), Gui.DimCol);
                Gui.Divider();
                DrawUpdateRow();

                // 调试分区：**双重门控**。DebugMode 只反映构建类型，ShowDebugSection
                // 要改源码才为 true——两个条件都要：调试构建不等于「想看见调试面板」，
                // 而正式发行的包里连这一块都不会被画出来。
                // 用 WarnCol 而不是 Accent：这一区里的开关能让回放播不了、提示不弹、
                // 存出不加密的文件，视觉上就该和「取消渲染」那类危险操作同色。
                if (DebugMode && ShowDebugSection)
                {
                    DrawDebugSection();
                }
            });
        }

        /// <summary>
        /// 关于页的调试分区，只在 <see cref="DebugMode"/> 与 <see cref="ShowDebugSection"/>
        /// 同时为真时绘制。
        ///
        /// <para><b>这些开关一律不持久化。</b>它们改的是本次运行的进程内状态，关模组或
        /// 重启游戏就回到默认值——所以这里刻意不调 <c>Commit()</c>，也没有对应的配置项。
        /// 理由见 SpectreState 里那组字段的注释：四种效果（跳过校验、不弹提示、存不加密
        /// 文件、走旧判定）一旦留在玩家机器上，都会表现为「好好的回放播不了」而且无从自查。</para>
        ///
        /// <para><b>为什么还要 ShowDebugSection 这道门。</b>调试构建在日常游玩里并不罕见
        /// （编辑器、开发版），可上面这些开关对普通玩家只有坏处。所以「调试构建」和
        /// 「愿意看见调试面板」分成两件事：前者自动判定，后者只能改源码。</para>
        /// </summary>
        private static void DrawDebugSection()
        {
            Gui.Section("about.debug", Gui.WarnCol, () =>
            {
                OnGui.Note("about.debug.note");
                OnGui.Row("about.debug.skipverify", debug_skip_Verification,
                    v => debug_skip_Verification = v);
                OnGui.Row("about.debug.hidetext", debug_hide_text,
                    v => debug_hide_text = v);
                OnGui.Row("about.debug.noencryption", debug_no_encryption,
                    v => debug_no_encryption = v);
                OnGui.Row("about.debug.oldhit", debug_use_old_hit,
                    v => debug_use_old_hit = v);
            });
        }

        /// <summary>
        /// 「检查更新」这一行。
        ///
        /// **这里不自己发网络请求。** 更新检查由 UMM 自己做：它读 info.json 的
        /// Repository 下载那个 JSON，把结果写进 ModEntry.NewestVersion，再在
        /// UMM 自己的模组列表里显示「(Outdated)」。我们只是把那个结果**读出来**
        /// 摆在关于页上，让玩家不必去翻 UMM 列表。
        ///
        /// 查证依据（反编译 UnityModManager.dll 的 IL，不是看文档）：
        ///   · CheckModUpdates 读 Info.Repository 去下载、读 Info.HomePage 供 Nexus 路径；
        ///   · ParseRepository 用 Release.Id 调 FindMod，而 FindMod 比的是 **ModInfo.Id**
        ///     ——所以 repo.json 里必须写 Id = com.spectre.replay，不是显示名；
        ///   · NewestVersion 只在「版本确实更新」时才被赋值，否则保持 null。
        ///
        /// 三种态各自说清楚，因为它们的修法完全不同：
        ///   · 有新版 → 告诉版本号，并让玩家能点开仓库下载；
        ///   · 查过且没新版 → 敢说「已是最新」；
        ///   · 还没查过 → **绝不要说「已是最新」**。把「UMM 根本没查」说成「已最新」，
        ///     玩家会永远等不到更新，而且没有任何迹象。
        ///
        /// **⚠️ NewestVersion == null 不能当作「已是最新」用。** 这是反编译 IL 才发现的：
        /// ParseRepository 里 `op_LessThan(current, release)` 之后紧跟 `brfalse`，
        /// 也就是**只有「确实更新」时才执行 stfld NewestVersion**；版本相等或更低时
        /// 直接跳过，字段保持 null。所以 null 同时代表「没新版」与「没查过」。
        /// 要分开这两种，唯一的旁证是 `Params.LastUpdateCheck`——UMM 每次检查都会
        /// 先把它写成 DateTime.Now。它非默认值 ⇒ 查过；此时 NewestVersion 仍为 null
        /// ⇒ 真的是最新。
        /// </summary>
        private static void DrawUpdateRow()
        {
            string homepage = HomePageUrl();
            Version newest = NewestVersionOrNull();
            Version current = CurrentVersionOrNull();
            bool checkedOnce = LastUpdateCheckSeen();

            if (newest != null && current != null && newest > current)
            {
                Gui.KeyValue("about.update.available",
                    Loc.T("about.update.available.value") + newest, Gui.WarnCol);
            }
            else if (checkedOnce)
            {
                Gui.KeyValue("about.update.status", Loc.T("about.update.latest"), Gui.OkCol);
            }
            else
            {
                Gui.KeyValue("about.update.status", Loc.T("about.update.unknown"), Gui.DimCol);
            }

            // 仓库按钮：只在真的填了 HomePage 时才画。没填还画一颗点不动的按钮，
            // 玩家会以为模组坏了。
            if (!string.IsNullOrEmpty(homepage))
            {
                OnGui.Button("about.update.open", () => OpenUrl(homepage), false);
            }
        }

        private static bool LastUpdateCheckSeen()
        {
            try
            {
                var umm = typeof(UnityModManagerNet.UnityModManager);
                var prop = umm.GetProperty("Params",
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                object ps = prop != null ? prop.GetValue(null, null) : null;
                if (ps == null) return false;
                var fld = ps.GetType().GetField("LastUpdateCheck",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                if (fld == null) return false;
                object v = fld.GetValue(ps);
                if (v is DateTime dt)
                {
                    // 默认值 DateTime.MinValue（从没查过）；任何真实时间都远大于它。
                    return dt.Year > 2000;
                }
                return false;
            }
            catch (Exception ex)
            {
                // 反射失败不能把整个设置页带走（OnGUI 抛异常 = 面板被 UMM 收掉）。
                Debug.LogWarning("[SpectreReplay] 读取上次更新检查时间失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>info.json 里填的 HomePage。取不到就返回空串（调用方据此决定不画按钮）。</summary>
        private static string HomePageUrl()
        {
            try
            {
                return Main.mod != null && Main.mod.Info != null
                    ? Main.mod.Info.HomePage ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// UMM 查到的"最新版本"。**可能为 null**——那表示"UMM 没查到"或"就是最新"，
        /// 两种情况在这里分不开，所以调用方不许把它当成"已是最新"。
        /// </summary>
        private static Version NewestVersionOrNull()
        {
            try
            {
                return Main.mod != null ? Main.mod.NewestVersion : null;
            }
            catch
            {
                return null;
            }
        }

        private static Version CurrentVersionOrNull()
        {
            try
            {
                // **ModEntry.Version 是 System.Version，ModInfo.Version 是 string。**
                // 拿错会编译不过——而这条路径只在"比较版本"时才走到，
                // 用 Info.Version 就得自己 Parse，多一层失败可能。
                return Main.mod != null ? Main.mod.Version : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 打开外部链接。包 try：Application.OpenURL 在无浏览器/被系统拦截时会抛，
        /// 而 OnGUI 抛异常的后果是整个设置面板被 UMM 收掉（玩家感受是"点一下就没了"）。
        /// </summary>
        private static void OpenUrl(string url)
        {
            try
            {
                Application.OpenURL(url);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 打开链接失败：" + ex.Message);
            }
        }

        private static string VersionText()
        {
            try
            {
                return Main.mod != null && Main.mod.Info != null
                    ? Main.mod.Info.Version.ToString() : "?";
            }
            catch
            {
                return "?";
            }
        }

        private static string SaveDirText()
        {
            try
            {
                string dir = SavePath;
                if (string.IsNullOrEmpty(dir) && Main.mod != null)
                {
                    dir = Main.mod.Path;
                }
                return string.IsNullOrEmpty(dir) ? "?" : dir;
            }
            catch
            {
                return "?";
            }
        }

        private static string BreakPathForDisplay(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\u200b").Replace("/", "/\u200b");
        }

        /// <summary>把过长的路径折成「头…尾」，保证单行显示得完。</summary>
        private static string ClipMiddle(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
            {
                return s;
            }
            int keep = Math.Max(1, max - 1);
            int head = keep / 2;
            int tail = keep - head;
            return s.Substring(0, head) + "\u2026" + s.Substring(s.Length - tail);
        }
    }
}
