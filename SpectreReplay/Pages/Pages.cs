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
    /// 设置页内容（UMM OnGUI）。外观一律走 <see cref="Gui"/> 的分组卡片，
    /// 这里只负责"哪一页有哪些行"。
    ///
    /// **三条规矩，每一条都对应一类真实故障：**
    ///   1. 玩家可见的文字一律进 Loc 表。写死中文的话，切成英文的玩家
    ///      会在英文界面里看到夹进来的中文，而这在中文环境下永远看不出。
    ///   2. 每个控件行必须真的接到一个设置字段。"画了一行但没接线"比不画更糟：
    ///      玩家以为改了什么，实际什么都没发生。
    ///   3. 页面体一律包 try。UMM 对模组 OnGUI 的异常处理是
    ///      LogException + ShowModSettings = -1 + GUIUtility.ExitGUI()，
    ///      等于**整个设置面板当场消失**——玩家的感受是"点一下窗口没了"。
    /// </summary>
    internal static partial class Pages
    {
        /// <summary>
        /// 当前载入的回放文件。
        ///
        /// 自己记而**不去问回放引擎**：ReplayData 只装数据，
        /// 不记得自己来自哪个文件。列表里那个"● 已载入"标记要靠它，
        /// 否则玩家放了哪一个只能靠猜。
        /// </summary>
        private static string _loadedPath = "";

        /// <summary>
        /// 当前载入的回放若来自云端，这里是它的云端文件名（空 = 不是从云端载入的）。
        ///
        /// <para>与 <see cref="_loadedPath"/> 并存而不是合并成一个字段：云端那份
        /// **没有本地路径**（整条链路不落盘），而本地那份有。两个标记各自只描述
        /// 一种来源，界面上各高亮自己那一栏；合并的话就必须给云端编一个假路径，
        /// 之后每一处按路径做事的代码（删除、详情面板）都得先认出这个假值。</para>
        /// </summary>
        private static string _loadedCloudName = "";

        /// <summary>
        /// 外部路径（渲染成功等）卸载回放后同步视图标记。卸载动作本身只清
        /// SpectreState 的数据，列表徽章与「卸载回放」按钮读的是这两个来源
        /// 字段——不同步的话，列表会把已卸载的回放一直标成「已载入」
        /// （2026-11-07 渲染成功实测）。字段保持 private，外部只走这个入口，
        /// 两个一起清，防止调用方漏掉其中一个。
        /// </summary>
        internal static void NotifyReplayUnloaded()
        {
            _loadedPath = "";
            _loadedCloudName = "";
        }

        // 回放列表缓存与刷新时刻（TickCount 差值按无符号比较，回绕安全）。
        private static List<string> _replayCache;
        private static int _replayCacheTick = int.MinValue;

        /// <summary>
        /// 回放列表的搜索框。
        ///
        /// **只影响「列表显示哪些」，不落盘、不进 Settings。** 它是一个视图状态：
        /// 存进配置的话，玩家上次筛了个词、下次开游戏列表就是空的，而界面上
        /// 那个输入框可能还在视口外——看起来就是「回放全没了」。
        /// </summary>
        private static readonly TextField _replaySearch = new TextField();

        /// <summary>
        /// 云端回放列表的搜索框。**与本地那个是两套独立状态**：两个列表各筛
        /// 各的——本地筛了 save、切到下面云端也只剩 save 的话，看起来就像
        /// 云端备份少了一大半。同样只是视图状态，不进 Settings。
        /// </summary>
        private static readonly TextField _cloudSearch = new TextField();

        /// <summary>
        /// 正在等第二次点击确认删除的那个文件（空 = 没有待确认的删除）。
        ///
        /// **删除只能两段式**：第一下把按钮变成「确认删除」并把路径记在这里，
        /// 第二下才真删。IMGUI 里没有宿主级确认框（UMM 不提供），而
        /// 回放文件是玩家的**唯一产出**、删掉不可恢复（没有回收站、没有撤销），
        /// 一颗单点即删的按钮放在每行——行里还有「回放」那颗主按钮——
        /// 手滑一次就没了。
        ///
        /// 存路径而不是 bool：玩家点 A 再点 B 时，B 必须是**第一次**点击
        /// （若只存 bool，B 一点就直接删了）。
        /// </summary>
        private static string _pendingDelete = "";

        /// <summary>
        /// 云端待确认删除的那一份（云端文件名）。
        ///
        /// 与 <see cref="_pendingDelete"/> 分开记：两者的确认态互不相干。共用一个
        /// 变量会出现「本地删到待确认，云端那行也跟着变成确认删除」——玩家没点过
        /// 云端那颗按钮，却看见它变成红色确认态，接着再点一下云端文件就没了。
        /// </summary>
        private static string _pendingCloudDelete = "";

        // ── 去特效预设（多配置） ────────────────────────────────
        //
        // 预设列表有缓存：目录扫描是每帧 I/O，不能画一遍扫一遍。
        // 保存/删除后 Force=true 立即失效，其余时候 2 秒内复用。
        private static readonly TextField _presetName = new TextField();
        private static readonly List<string> _presetFiles = new List<string>();
        private static string[] _presetOptions = Array.Empty<string>();
        private static int _presetSel;
        private static int _presetCacheTick = int.MinValue;
        // 删除沿用回放列表的两段确认：记文件名而不是 bool——玩家点 A 再点 B
        // 时，B 必须重新走第一段（见 _pendingDelete 的注释）。
        private static string _presetDeleteArm = "";
        private static int _presetDeleteArmTick;

        /// <summary>
        /// 「从云端取回配置」是否处在待确认态（空 = 没有待确认的取回）。
        ///
        /// **必须与上面两个确认态分开记。** 取回会**覆盖本机配置文件**，是这块里
        /// 唯一会动玩家自己设置的动作；与删除共用变量会出现「点了删除确认，
        /// 取回那颗也跟着变红」——反过来也一样，两者互相串联的后果是玩家在
        /// 一颗自己没点过的按钮上按一下，设置就被云端那份顶掉了。
        /// </summary>
        private static string _pendingConfigRestore = "";

        /// <summary>
        /// 云端配置备份元信息的缓存（有没有 / 多大 / 什么时候）。
        ///
        /// 与 <see cref="CloudReplay.GetStats"/> 同一个理由：这个状态行画在
        /// IMGUI 每帧的回调里（一帧 Layout / Repaint 好几趟），而
        /// <c>TryGetConfigBackupInfo</c> 底下是一次 SteamCloud.List 的 P/Invoke
        /// （枚举云端全部文件）。不缓存就等于停在这一页时每秒几十次系统调用。
        /// 一秒过期：手动上传/取回后由 <see cref="InvalidateConfigBackupInfo"/>
        /// 立刻作废，玩家看到的是即时结果，不是"过一秒才变"。
        /// </summary>
        private static bool _cfgInfoHas;
        private static int _cfgInfoSize;
        private static uint _cfgInfoTimestamp;
        private static int _cfgInfoTick = int.MinValue;


        /// <summary>字节数 → 便于阅读的大小（回放普遍在几十 KB～几 MB）。</summary>
        private static string SizeText(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1") + " GB";
            if (bytes >= 1024L * 1024L)
                return (bytes / 1024.0 / 1024.0).ToString("F1") + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("F0") + " KB";
            return bytes + " B";
        }

        // ── 工具 ────────────────────────────────────────────────
        /// <summary>拖动滑条期间被推迟的落盘。见 Commit 的说明。</summary>
        private static bool _pendingCommit;

        internal static void RefreshAfterConfigRestore()
        {
            // 显式恢复不是「把旧界面待办也重放一遍」。新配置已应用/保存，旧草稿作废。
            _pendingCommit = false;
            CancelCaptures();
        }

        /// <summary>
        /// 改一个设置后落盘 + 刷新补丁开关。
        ///
        /// 必须包 try：OnGUI 里抛异常会被 UMM 吞掉并把**整个设置面板收掉**，
        /// 表现是"点一下窗口没了"，而不是一条错误。
        ///
        /// **拖动滑条时只记待办、不落盘。** 滑条的值每帧都在变，onChange 于是每帧
        /// 都调到这里；一次拖动 = 几十次"全量序列化 + Settings.check（解析键表 +
        /// 256 次循环）+ 全量 Refresh + 写文件"。现场观感就是拖动一顿一顿的，
        /// 而这跟"设置存不下来"是两回事，很容易被当成同一个毛病去修。
        /// 字段本身当场就改了（滑条即时生效），所以推迟的只是**磁盘写入**；
        /// 松手那一帧由 FlushPending 补上。只推迟"按住鼠标"期间：按钮与下拉
        /// 在 MouseUp 那帧才触发，那帧 GetMouseButton(0) 已经是 false，照旧只写一次。
        /// </summary>
        private static void Commit()
        {
            if (Input.GetMouseButton(0))
            {
                _pendingCommit = true;
                return;
            }
            SaveNow();
        }

        /// <summary>补上拖动期间被推迟的落盘。force = 关窗/保存/退出时必须落地。
        /// 返回是否真的写了——调用方据此避免紧接着又写一遍。</summary>
        internal static bool FlushPending(bool force = false)
        {
            if (!_pendingCommit)
            {
                return false;
            }
            if (!force && Input.GetMouseButton(0))
            {
                return false;
            }
            SaveNow();
            return true;
        }

        private static void SaveNow()
        {
            _pendingCommit = false;
            try
            {
                if (Main.mod != null && !string.IsNullOrEmpty(Main.mod.Path))
                {
                    ConfigManager.SaveConfigs(Path.Combine(Main.mod.Path, "Configs.json"));
                    Settings.check(Main.mod.Path);
                }
                SpectrePatchHub.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 提交设置失败：" + ex.Message);
            }
        }

        /// <summary>作废回放列表缓存（新增/回填文件后必须调，否则界面要等一秒才刷新）。</summary>
        internal static void InvalidateReplayList()
        {
            _replayCache = null;
            _replayCacheTick = int.MinValue;
        }

        /// <summary>
        /// 回放文件列表，按修改时间倒序。
        ///
        /// **必须带缓存。** 这个方法是在 IMGUI 每帧（一帧要跑 Layout / Repaint 好几趟）
        /// 画列表的回调里调的；原实现每趟都 Directory.GetFiles + Array.Sort，而那个
        /// 比较器每次都调 File.GetLastWriteTimeUtc —— 等于每帧 O(n log n) 次系统调用。
        /// 回放攒到几百个时，光是停在这一页就会持续掉帧，而且很容易被误判成
        /// “渲染在跑”/“游戏卡”。缓存一秒：开销归零，新录一局后一秒内也会出现。
        ///
        /// 返回的是缓存本体，**调用方只许读、不要改**。
        /// </summary>
        internal static List<string> ReplayList()
        {
            if (_replayCache != null
                && (uint)(Environment.TickCount - _replayCacheTick) < 1000u)
            {
                return _replayCache;
            }
            var list = new List<string>();
            try
            {
                string dir = SavePath;
                if (string.IsNullOrEmpty(dir) && Main.mod != null)
                {
                    dir = Main.mod.Path;
                }
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    _replayCache = list;
                    _replayCacheTick = Environment.TickCount;
                    return list;
                }
                var files = Features.Replay.ReplayManager.ReadableExtensions
                    .SelectMany(ext => Directory.GetFiles(dir, "*" + ext))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Array.Sort(files, (a, b) =>
                    File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                list.AddRange(files);
            }
            catch
            {
                // 目录不可读就当作没有回放
            }
            _replayCache = list;
            _replayCacheTick = Environment.TickCount;
            return list;
        }
    }
}
