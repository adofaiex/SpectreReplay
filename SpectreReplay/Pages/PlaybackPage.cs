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
    /// 回放页：当前回放状态、回放列表（搜索 / 载入 / 删除 / 详情）、云端列表、
    /// 键盘音覆盖、开局信息与计时统计。列表缓存与落盘仍在 Pages。
    /// </summary>
    internal static partial class Pages
    {
        // ── 回放 ────────────────────────────────────────────────
        internal static void DrawPlayback()
        {
            // **「模拟按键」开关（原 Settings.LegacyEngine）已删除。**
            //
            // 它看起来是「要不要注入按键」，实际关掉后并非「只播不注入」：
            // 判定覆盖、长条 holdKeys 同步、键位显示桥接全都会跟着停，
            // 于是长条关卡莫名失败、KeyViewer 变黑，而画面上看不出原因。
            // 一个「关掉就坏」的开关摆在回放页第一屏，等于给所有用户
            // 准备了一个查不出来的坑。
            //
            // 真要停止注入，用 ReplayOn 总开关——它连补丁一起卸，语义干净。
            Gui.Section("playback.current", Gui.OkCol, () =>
            {
                DrawPlaybackState();
                OnGui.Row("timeline.enable", Settings.TimelineOn,
                    v => { Settings.TimelineOn = v; Commit(); });
                // 结算「最大同时按键数」的显示门是游戏写死的 >10；打开后由
                // Patch_AlwaysShowMaxUsedKeys 把这个条件改掉，任何一局都显示。
                // 放在回放页而不是关于页：它是复盘时看的结算信息，玩家会在
                // 这里找「为什么我这一局没看到最大按键数」。
                OnGui.Row("replay.maxusedkeys", Settings.AlwaysShowMaxUsedKeys,
                    v => { Settings.AlwaysShowMaxUsedKeys = v; Commit(); });
                // 结算/回放面板的「3.3.1 口径」精准度标注开关：打开后 3.4.0 本机
                // 在结算精准度行内联 (3.3.1: xx.xx%) 并在回放信息面板加跨口径行
                //（Patch_CrossEraAccuracy 的 Feature 门 + ReplayInfoPanel）。
                OnGui.Row("replay.crosseraacc", Settings.CrossEraAccuracy,
                    v => { Settings.CrossEraAccuracy = v; Commit(); });
            });

            var files = ReplayList();
            Gui.Section("playback.files", Gui.Accent, () =>
            {
                if (files.Count == 0)
                {
                    OnGui.Note("playback.empty");
                    return;
                }
                // 搜索框在**滚动区外面**：它决定「列表显示哪些」，跟着列表一起
                // 滚出视野的话，每次改筛选词都得先把列表滚回顶部。
                DrawSearchRow(_replaySearch);
                var listed = FilterReplays(files);
                if (listed.Count == 0)
                {
                    // 有回放、但都被筛掉了：必须说清是「筛掉了」而不是「没有」，
                    // 否则玩家会以为文件丢了（它们还在目录里、云备份也照传）。
                    OnGui.NoteText(Loc.F("playback.search.nomatch", _replaySearch.Text));
                    return;
                }
                // 列表外面套一层**固定高度**的滚动区。行是逐行铺开的，几十行
                // 就是一两千像素：不封顶时整页会被顶得老长，列表下面的云备份
                // 区块永远在屏幕之外。封顶后列表自己滚，整页长度就定住了。
                // **这就是方案的全部**：列表在自己的框里滚，整页那一层仍交给
                // UMM 自带的滚动条，一像素都不去改它的外观。
                //
                // 列表里有多少就画多少——「只显示了一部分」这件事已经不存在，
                // 所以也不再有截断提示：那是条数上限的伴生文案，上限删了它就该走。
                Gui.ScrollArea("playback.files", 300f, () =>
                {
                    foreach (string path in listed)
                    {
                        // **这里没有条数上限。** 原来有一句 `if (shown++ >= 200) break;`，
                        // 理由是「几百上千行、每行两颗按钮会持续掉帧」。代价是玩家
                        // 确实有的回放看不见：文件在目录里、云备份也在传，界面上就是
                        // 少了一半且一个字都不解释。
                        // 按玩家要求整条删掉：列表里有的 = 磁盘上有的。掉帧风险由
                        // 上面的搜索框兜——它把「找某一份」的成本降到打几个字。
                        string captured = path;
                        bool loaded = string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase);
                        GUILayout.BeginHorizontal(GUILayout.Height(30f));
                        if (loaded)
                        {
                            string loadedBadge = Loc.T("playback.loaded");
                            Gui.Badge(Gui.BadgeRect(loadedBadge, 46f, 20f),
                                loadedBadge, Gui.OkCol);
                        }
                        // 文件名本身就是展开详情的开关。**不另加一颗「详情」按钮**：
                        // 这一行已经有两颗按钮（回放 / 删除），再加一颗会让「点名字」
                        // 这个玩家的自然动作失效——点了没反应，还得再找那颗小按钮。
                        Gui.ClickableLabel(Path.GetFileNameWithoutExtension(path),
                            loaded || ReplayInfoPanel.IsOpen(captured),
                            "playback.info.tip",
                            () => ReplayInfoPanel.Toggle(captured), 30f);
                        // 宽度按文案实测：写死常量在英文/西语下会把字裁掉
                        // （Gui.ButtonW 的注释里记了那次事故）。
                        string replayLabel = Loc.T(loaded ? "playback.unload" : "playback.replay");
                        if (GUILayout.Button(replayLabel, Gui.ButtonStyle,
                                GUILayout.Width(Gui.ButtonW(replayLabel, Gui.ButtonStyle, 110f)),
                                GUILayout.Height(26f)))
                        {
                            if (loaded)
                            {
                                PlayActions.UnloadReplay();
                                _loadedPath = "";
                                _loadedCloudName = "";
                            }
                            else LoadReplayFile(captured);
                        }
                        // 云端单份上传：**只在云可用时出现**。云关着时给每一行都挂一颗
                        // 灰按钮，等于用几十颗灰点去解释一件「这一页别处已经说清」的事，
                        // 而列表里行的首要信息是文件名。
                        if (CloudReady) DrawUploadButton(captured);
                        // **删除放在最后一列、用最轻的样式。** 这一行已经有
                        // 「回放」那颗主按钮，删除是破坏性操作，不该和它抢位置与
                        // 视觉权重；用 QuietButtonStyle 与「卸载回放」同一档。
                        DrawDeleteFileButton(captured);
                        GUILayout.EndHorizontal();
                        if (Gui.ModernMode) GUILayout.Space(4f);

                        // 详情就地展开在这一行正下方（点文件名切换）。
                        if (ReplayInfoPanel.IsOpen(captured))
                        {
                            ReplayInfoPanel.Draw(captured);
                        }
                    }
                });
            });

            DrawCloudSection();
        }

        /// <summary>
        /// 回放列表的搜索行：按文件名过滤。**本地与云端两个列表共用这一份实现**，
        /// 各自传自己的 <see cref="TextField"/>——两边的交互（边打边筛、Esc 清空、
        /// 清除按钮、落空提示）没有理由不一样，分成两份迟早会走偏。
        ///
        /// <para><b>为什么要有它。</b>回放只增不减，攒到几百份之后「找上次录的
        /// 那一局」只能靠肉眼在列表里翻；搜索把这件事变成打几个字。</para>
        ///
        /// <para><b>为什么边打边生效、不需要回车。</b>这个框唯一的用途是缩小
        /// 列表，回车在这里没有语义（不像输出目录那样要落盘）。TextField.Text
        /// 是逐字符更新的，直接读它就是实时筛选。</para>
        ///
        /// <para><b>为什么清除按钮要单独给。</b>Esc 也能清空（见下），但「框里
        /// 有字」时玩家第一反应是找一个叉；而 Esc 这个约定并不会写在界面上，
        /// 切到英文界面更是没有任何提示。</para>
        /// </summary>
        private static void DrawSearchRow(TextField field)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("playback.search"), Gui.LabelStyle, GUILayout.Width(Gui.LabelW));
            Rect box = GUILayoutUtility.GetRect(Gui.CtrlW, 24f,
                GUILayout.Width(Gui.CtrlW), GUILayout.Height(24f));
            Gui.Tip(box, "playback.search.tip");
            string clearLabel = Loc.T("playback.search.clear");
            if (GUILayout.Button(clearLabel, Gui.ButtonStyle,
                    GUILayout.Width(Gui.ButtonW(clearLabel, Gui.ButtonStyle, 90f)),
                    GUILayout.Height(24f)))
            {
                ClearSearch(field);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 与 OnGui.RowInput 同理：输入框自绘、不参与 GUILayout 布局，
            // 必须画在 EndHorizontal 之后，否则它的鼠标处理会和水平组打架。
            bool canceled;
            field.Draw(box, Gui.LabelStyle, Loc.T("playback.search.empty"), out canceled);
            if (canceled)
            {
                // Esc = 放弃这次输入。搜索框没有「已保存的值」可以还原，
                // 所以这里的取消语义只能是清空。
                ClearSearch(field);
            }
            GUILayout.Space(4f);
        }

        /// <summary>清空搜索框并让它失焦（Esc 与「清除」按钮共用一处）。</summary>
        private static void ClearSearch(TextField field)
        {
            field.Text = "";
            field.Editing = false;
        }

        /// <summary>
        /// 按文件名过滤回放列表（不区分大小写）；空查询直接返回原列表本体。
        ///
        /// **只匹配文件名、不匹配整条路径**：列表里显示的是文件名，玩家输入的
        /// 也只会是文件名；拿整条路径去匹配会让「搜 save」这类词命中一堆目录名，
        /// 结果看起来毫无规律。
        /// </summary>
        private static List<string> FilterReplays(List<string> files)
        {
            string query = (_replaySearch.Text ?? "").Trim();
            if (query.Length == 0)
            {
                return files;   // 不复制：上游只读缓存本体
            }
            var hit = new List<string>();
            for (int i = 0; i < files.Count; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                if (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit.Add(files[i]);
                }
            }
            return hit;
        }

        /// <summary>
        /// 按文件名过滤云端回放列表（不区分大小写）；空查询直接返回原列表本体。
        ///
        /// 判据与 <see cref="FilterReplays"/> 一模一样——只匹配文件名、不匹配目录。
        /// 云端条目只在 <c>Name</c> 里带文件名，两边因此天然对齐。
        /// </summary>
        private static List<CloudReplay.CloudEntry> FilterCloudReplays(
            List<CloudReplay.CloudEntry> entries)
        {
            string query = (_cloudSearch.Text ?? "").Trim();
            if (query.Length == 0)
            {
                return entries;   // 不复制：上游这个列表每帧重建，没人长期持有
            }
            var hit = new List<CloudReplay.CloudEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                string name = Path.GetFileNameWithoutExtension(entries[i].Name);
                if (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit.Add(entries[i]);
                }
            }
            return hit;
        }

        /// <summary>
        /// Steam 云备份区块。**放在回放列表之后、而不是录制页**：它管理的是
        /// 已经存在的回放文件（上传 / 找回），和上面的列表是同一件事的两半——
        /// 玩家在这里看见文件、在这里决定要不要备份，不用来回跳页。
        /// </summary>
        private static void DrawCloudSection()
        {
            Gui.Section("playback.cloud", Gui.Accent, () =>
            {
                OnGui.Row("playback.cloud.sync", CloudSyncOn,
                    v => { CloudSyncOn = v; Commit(); });
                // 自动回填只在同步开着时才有意义（云端没有新文件，回填也就无从谈起）
                OnGui.Row("playback.cloud.restoreonload", CloudRestoreOnLoad,
                    v => { CloudRestoreOnLoad = v; Commit(); }, CloudSyncOn);
                DrawCloudStatus();
                DrawCloudReplays();
                OnGui.Button("playback.cloud.restore", () => QueueCloudAction(true), false, CloudReady);
                OnGui.Button("playback.cloud.uploadall", () => QueueCloudAction(false), false, CloudReady);
                DrawCloudConfigBlock();
                OnGui.Note("playback.cloud.note");
            });
        }

        /// <summary>
        /// 云端回放列表：**点名字展开就地详情（读云端字节），播放走那颗显式「回放」
        /// 按钮**——与本地列表完全同一套行交互。详情也不落地，所以「看一眼」不会
        /// 把玩家正在放的回放顶掉。
        ///
        /// <para>为什么值得单独一块。云备份原先只是「保险」——本地丢了才去回填。
        /// 但换机/重装之后玩家真正想做的事往往是「接着看上周那局」，而回填会把
        /// 云端那份复制进本地目录：看得越多目录越脏，回填的判据（本地已有就跳过）
        /// 也会被这些副本搅乱。这里直接拿云端字节播，播完桌上什么都不多。</para>
        ///
        /// <para>没有条数上限，理由与本地列表一致：列出来的就是云端真有的。
        /// 顺序由 <see cref="CloudReplay.ListCloudReplays"/> 定（文件名带时间戳，
        /// 倒序即最新在前），UI 不再排一次——两处各排一次迟早会不一致。</para>
        ///
        /// <para>空列表什么都不画：上面的状态行已经说了「云端还没有回放备份」或者
        /// 「Steam 云不可用」，再补一句只会把同一件事说两遍。**筛到空是另一回事**：
        /// 备份都还在，只是当前筛选词没命中——那种情况必须说清，否则玩家会以为
        /// 云备份丢了。</para>
        /// </summary>
        private static void DrawCloudReplays()
        {
            if (!CloudReady) return;
            var entries = CloudReplay.ListCloudReplays();
            if (entries.Count == 0) return;

            GUILayout.Space(4f);
            GUILayout.Label(Loc.T("playback.cloud.list"), Gui.LabelStyle);
            // 搜索框与本地那栏共用同一份实现（DrawSearchRow），但**状态独立**：
            // 两边的文件名不是一一对应的（云端有、本地还没回填的那一份只在云端
            // 出现），共用一个筛选词只会让两边互相干扰。
            DrawSearchRow(_cloudSearch);
            var listed = FilterCloudReplays(entries);
            if (listed.Count == 0)
            {
                // 有备份、但都被筛掉了：与本地同一条理由——必须说清是「筛掉了」，
                // 否则玩家会以为云备份丢了（它们还在云端，换个筛选词就回来）。
                OnGui.NoteText(Loc.F("playback.search.nomatch", _cloudSearch.Text));
                return;
            }
            // 滚动区键**不能写成点分的键名形态**：LocCheck 会把页面里每个带引号的
            // 小写点分字面量都当成文案键去查（见 tools/UiPreview/LocCheck.cs 的正则），
            // 查不到就判「露键名」。而这个键只是滚动位置的字典键，天生不需要翻译——
            // 改成一个不像键的名字，两边都不别扭。
            // （注释里也别写带引号的那个旧键名，同一个正则会连注释一起扫。）
            Gui.ScrollArea("cloudReplayFiles", 180f, () =>
            {
                foreach (var entry in listed)
                {
                    CloudReplay.CloudEntry item = entry;
                    bool loaded = string.Equals(item.Name, _loadedCloudName,
                        StringComparison.OrdinalIgnoreCase);
                    GUILayout.BeginHorizontal();
                    if (loaded)
                    {
                        string loadedBadge = Loc.T("playback.loaded");
                        Gui.Badge(Gui.BadgeRect(loadedBadge, 46f, 20f),
                            loadedBadge, Gui.OkCol);
                    }
                    // 与本地列表同一套语义：**点名字展开就地详情，播放是那颗显式按钮**。
                    // 详情面板读的是云端字节（CloudReplay.PeekReplay），不装载、不落盘，
                    // 所以「看一眼」不会把玩家正在放的回放顶掉。
                    string capturedCloud = item.Name;
                    Gui.ClickableLabel(Path.GetFileNameWithoutExtension(item.Name),
                        loaded || ReplayInfoPanel.IsOpenCloud(capturedCloud),
                        "playback.info.tip",
                        () => ReplayInfoPanel.ToggleCloud(capturedCloud));
                    // 按钮与本地列表同一颗的两种状态：已载入的这一份变成
                    // 「卸载」，再点退回未装弹状态；卸载清两个来源字段——
                    // 只换文案不清状态的话，按钮会一直读作「重新装载」。
                    string cloudReplayLabel = Loc.T(loaded ? "playback.unload" : "playback.replay");
                    if (GUILayout.Button(cloudReplayLabel, Gui.ButtonStyle,
                            GUILayout.Width(Gui.ButtonW(cloudReplayLabel, Gui.ButtonStyle, 110f)),
                            GUILayout.Height(22f)))
                    {
                        if (loaded)
                        {
                            PlayActions.UnloadReplay();
                            _loadedPath = "";
                            _loadedCloudName = "";
                        }
                        else LoadReplayFromCloud(capturedCloud);
                    }
                    // 单份云操作。**与下面那两颗批量按钮不是替代关系**：批量是「存量
                    // 一次性对齐」，而玩家的日常是「我就要这一份——下载它 / 丢它」。
                    // 为了丢一份云端副本去跑一遍「全部」既慢又会顺带做别的事。
                    DrawCloudDownloadButton(item.Name);
                    DrawCloudDeleteButton(item.Name);
                    // **不要在这里补 FlexibleSpace 挤右对齐。** 可点标签自己就是
                    // ExpandWidth(true)，而在滚动区里再放一个弹性占位，两者会去抢
                    // 同一份剩余宽度——实测是标签被压到十几像素，文件名只能一个字
                    // 一个字地竖着换行。照本地列表的做法：可扩展的放前面，固定宽的
                    // 直接跟在后头，它自然会被顶到右边。
                    // 宽度按文案实测：俄语「1,39 МБ」比 76px 宽，写死会切掉末位。
                    string sizeText = SizeText(item.Size);
                    GUILayout.Label(sizeText, Gui.NoteStyle,
                        GUILayout.Width(Gui.ButtonW(sizeText, Gui.NoteStyle, 76f)));
                    GUILayout.EndHorizontal();

                    // 详情就地展开在这一行正下方（点文件名切换），与本地列表完全一致。
                    if (ReplayInfoPanel.IsOpenCloud(capturedCloud))
                    {
                        ReplayInfoPanel.DrawCloud(capturedCloud, item.Size, item.Timestamp);
                    }
                }
            });
            OnGui.Note("playback.cloud.list.note");
        }

        /// <summary>
        /// 从云端装载并立即切过去（不落盘）。
        ///
        /// <para>同步执行，与本地那颗「回放」按钮一致：一次 SteamCloud.Download 读的是
        /// Steam 客户端已经放在本机的云端缓存，与读一个本地回放文件同量级；批量上传/
        /// 回填才需要推迟到下一拍（那是几十次读写）。</para>
        ///
        /// <para>失败只弹一条提示 + 一行日志，不往界面上堆红字：点击是玩家的常规动作，
        /// 而云端文件坏掉是可预期的（旧版本写的、上传中断）。</para>
        ///
        /// <para><b>键盘音缺失必须说出来。</b>云端的 wav 可能没传上去、超限被跳过、
        /// 或哈希对不上——这三种在引擎眼里都只是「没声音」，画面照放。不说的话玩家
        /// 只会觉得「云端回放是无声的」，而这恰恰是最容易被当成玩法缺陷的一类。</para>
        /// </summary>
        private static void LoadReplayFromCloud(string remoteName)
        {
            _pendingDelete = "";
            try
            {
                if (!CloudReplay.LoadFromCloud(out ReplayData loaded, remoteName,
                        out string err, out bool keybdSoundLost))
                {
                    Debug.LogWarning("[SpectreReplay] 云端回放装载失败（" + err + "）：" + remoteName);
                    Status.Warn("playback.cloud.failed", CloudErrorText(err));
                    return;
                }

                ApplyLoadedReplay(loaded);
                _loadedPath = "";              // 云端这份没有本地路径
                _loadedCloudName = remoteName;

                string name = Path.GetFileNameWithoutExtension(remoteName);
                NotifyReplayLoaded(name, keybdSoundLost);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云端回放装载失败：" + ex);
                Status.Warn("playback.cloud.failed", ex.GetType().Name);
            }
        }

        /// <summary>
        /// 装载成功后的共同收尾：算「全量回放」标志、切到播放模式。
        ///
        /// <para>抽出来是因为现在有两个入口（本地文件 / 云端字节），而漏做任何一步的
        /// 现象都很隐蔽：不切播放模式 → 界面说「已装好」，重开关卡却不播；
        /// 不算 IsFullRun → 提示条对半截回放谎称「可任意位置开始」。</para>
        ///
        /// <para>IsFullRun 必须现算：这个标志原本只在关卡启动时（Patch_Lifecycle）赋值，
        /// 而设置页载入回放时还没进关，读到的永远是上一次的残留值。关卡没加载时
        /// ADOBase.lm 为 null，此时算不出来 → 保持 false，宁可少一条提示，
        /// 也不要对半截回放谎称「可任意位置开始」。</para>
        /// </summary>
        private static void ApplyLoadedReplay(ReplayData loaded)
        {
            // Decoding must finish first; on failure the old session is untouched.
            // Once valid bytes are ready, stop before replacing data so an active
            // recording's Stop path still saves its own data object.
            PlayActions.StopPlaying();
            PlayActions.StopRecording();
            data = loaded;
            IsFullRun = data.StartTile.HasValue && data.EndTile.HasValue
                && data.StartTile == 0
                && ADOBase.lm != null
                && ADOBase.lm.listFloors != null
                && ADOBase.lm.listFloors.Count > 0
                && data.EndTile == ADOBase.lm.listFloors.Count - 1;
            PlayActions.SwitchToPlayMode();
        }

        /// <summary>
        /// 一行的「上传」按钮：把这一份本地回放（含键盘音）单独传到云端。
        ///
        /// <para>与「全部上传」并存是刻意的：后者是刚开云功能时把存量铺上去的
        /// 一次性动作，而玩家真正常用的是「刚录的这局现在就传」——为了传一份
        /// 把几百份重扫一遍不合理。</para>
        /// </summary>
        private static void DrawUploadButton(string path)
        {
            string label = Loc.T("playback.cloud.upload.one");
            Rect r = Gui.ButtonRect(label, Gui.QuietButtonStyle, 70f, 26f);
            if (GUI.Button(r, label, Gui.QuietButtonStyle))
            {
                UploadReplayFile(path);
            }
            Gui.Tip(r, "playback.cloud.upload.one.tip");
        }

        /// <summary>把本地一份回放传到云端，并把「跳过 / 成功 / 失败」三种结果分开说。</summary>
        private static void UploadReplayFile(string path)
        {
            try
            {
                if (!CloudReplay.UploadOne(path, out string err, out bool alreadyCloud))
                {
                    Debug.LogWarning("[SpectreReplay] 单份上传云端失败（" + err + "）：" + path);
                    Status.Warn("playback.cloud.failed", CloudErrorText(err));
                    return;
                }
                string name = Path.GetFileNameWithoutExtension(path);
                if (alreadyCloud) Status.Say("playback.cloud.upload.exists", name);
                else Status.Say("playback.cloud.uploaded.one", name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 单份上传云端失败：" + ex);
                Status.Warn("playback.cloud.failed", ex.GetType().Name);
            }
        }

        /// <summary>
        /// 云端的「下载」按钮：把这一份（连配套键盘音）写回本地保存目录。
        ///
        /// **不需要两段确认**：它只是往本地加文件，而本地已有同名文件时不覆盖，
        /// 所以它毁不掉玩家手里的东西。这条判据与「删除」那一颗正好相反。
        /// </summary>
        private static void DrawCloudDownloadButton(string remoteName)
        {
            string label = Loc.T("playback.cloud.download");
            Rect r = Gui.ButtonRect(label, Gui.QuietButtonStyle, 70f, 22f);
            if (GUI.Button(r, label, Gui.QuietButtonStyle))
            {
                DownloadCloudReplay(remoteName);
            }
            Gui.Tip(r, "playback.cloud.download.tip");
        }

        private static void DownloadCloudReplay(string remoteName)
        {
            try
            {
                if (!CloudReplay.DownloadOne(remoteName, out string err, out bool alreadyLocal))
                {
                    Debug.LogWarning("[SpectreReplay] 云端单份下载失败（" + err + "）：" + remoteName);
                    Status.Warn("playback.cloud.failed", CloudErrorText(err));
                    return;
                }
                string name = Path.GetFileNameWithoutExtension(remoteName);
                if (alreadyLocal) Status.Say("playback.cloud.download.exists", name);
                else Status.Say("playback.cloud.downloaded", name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云端单份下载失败：" + ex);
                Status.Warn("playback.cloud.failed", ex.GetType().Name);
            }
        }

        /// <summary>
        /// 云端的「删除」按钮（两段确认）：**只删云端那份，本地文件原样保留**。
        ///
        /// <para>为什么非要确认：云端是玩家换机器后唯一的副本，删掉就再也找不回来；
        /// 而它又与本地那颗删除长得几乎一样，不设确认极容易点串。</para>
        /// </summary>
        private static void DrawCloudDeleteButton(string remoteName)
        {
            bool pending = string.Equals(remoteName, _pendingCloudDelete, StringComparison.OrdinalIgnoreCase);
            // 与本地那颗同一个组件：外观一致，才不会让玩家把云端删除当成"点了没反应"。
            if (!ConfirmButton.Draw(pending, "playback.delete", "playback.delete.confirm",
                    "playback.cloud.delete.tip", 70f, 26f))
            {
                return;
            }
            if (!pending)
            {
                _pendingCloudDelete = remoteName;   // 第一下只进确认态，云端一个字节都不动
                return;
            }
            _pendingCloudDelete = "";
            DeleteCloudReplay(remoteName);
        }

        private static void DeleteCloudReplay(string remoteName)
        {
            try
            {
                if (!CloudReplay.DeleteOne(remoteName, out string err, out bool wavDeleted))
                {
                    Debug.LogWarning("[SpectreReplay] 云端单份删除失败（" + err + "）：" + remoteName);
                    Status.Warn("playback.cloud.failed", CloudErrorText(err));
                    return;
                }
                // 键盘音有没有跟着删掉只写日志：界面上「删掉了」是同一件事，
                // 而旧回放本来就可能没有 wav，写成两句话只会让人以为漏删了。
                Debug.Log("[SpectreReplay] 已从云端删除 " + remoteName + (wavDeleted ? "（含键盘音）" : "（无配套键盘音）"));
                Status.Say("playback.cloud.deleted", Path.GetFileNameWithoutExtension(remoteName));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 云端单份删除失败：" + ex);
                Status.Warn("playback.cloud.failed", ex.GetType().Name);
            }
        }
        private static void LoadReplayFile(string path)
        {
            _pendingDelete = "";
            try
            {
                if (!Features.Replay.ReplayManager.LoadReplayAuto(
                        out ReplayData loaded, path))
                {
                    Status.Warn("playback.idle_err", Path.GetFileNameWithoutExtension(path));
                    return;
                }
                ApplyLoadedReplay(loaded);
                _loadedPath = path;
                _loadedCloudName = "";          // 换成本地这份：云端标记作废
                NotifyReplayLoaded(Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 载入回放失败：" + ex);
            }
        }

        private static void NotifyReplayLoaded(string name, bool keybdSoundLost = false)
        {
            // 允许装载/查看，但总开关关闭时生命周期补丁未挂；不能承诺重开会播。
            if (!Settings.ReplayOn)
            {
                Status.Warn(keybdSoundLost ? "status.loaded_disabled_nosound" : "status.loaded_disabled", name);
                return;
            }
            if (keybdSoundLost) Status.Warn("playback.cloud.play.nosound", name);
            else Status.Say(PlayMode && !is_playing ? "status.pending_replay" : "status.loaded", name);
        }

        /// <summary>
        /// 每行末尾那颗「删除」按钮。**两段式**：第一下只把按钮文案换成
        /// 「确认删除」，第二下才真的删文件。
        ///
        /// <para><b>为什么必须两段。</b>回放文件是玩家的唯一产出，删掉不可恢复
        /// （不走回收站、没有撤销）。而它所在的那一行左边就是「回放」主按钮，
        /// 手滑一次那局就没了。UMM 不提供宿主级确认框，所以在原地做。</para>
        ///
        /// <para><b>为什么记路径而不是 bool。</b>玩家先点 A 再点 B 时，B 必须是
        /// 「第一次点击」。只存 bool 的话 B 一点就直接删——两次点击落在两个
        /// 不同文件上，语义完全不同。</para>
        ///
        /// <para><b>为什么删的若是当前载入的那个要连带卸载。</b>文件没了而
        /// <c>data</c> 还装着它的内容，界面会一直显示「已载入」一个不存在的
        /// 文件；玩家再点重开还能播——这会让人以为删除失败了。</para>
        /// </summary>
        private static void DrawDeleteFileButton(string path)
        {
            bool pending = string.Equals(path, _pendingDelete, StringComparison.OrdinalIgnoreCase);
            // 外观与点击协议交给 ConfirmButton（与云端删除、预设删除共用一份）；
            // 两段式的判定留在这里——它才是这条按钮的业务语义。
            if (!ConfirmButton.Draw(pending, "playback.delete", "playback.delete.confirm",
                    "playback.delete", 70f, 26f))
            {
                return;
            }
            if (!pending)
            {
                // 第一下：只进入待确认态，绝不碰文件。
                _pendingDelete = path;
                return;
            }

            // 第二下：真删。
            _pendingDelete = "";
            try
            {
                File.Delete(path);
                AfterReplayDeleted(path);
                Status.Say("playback.delete.done", Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 删除回放失败：" + ex);
                Status.Warn("playback.delete.err", Path.GetFileNameWithoutExtension(path));
            }
        }

        /// <summary>
        /// 文件删掉之后的连带处理。**两件事都必须做，且都不是可选的：**
        ///
        /// ① 删的若是当前载入那份，必须卸载并清 <c>_loadedPath</c>。否则界面
        ///    一直显示「已载入」一个已经不存在的文件，而玩家再点「重开这关」
        ///    还能播（数据在内存里）——看起来就像删除失败了。
        /// ② 必须刷列表缓存。列表有 1 秒缓存，不刷的话被删的那行还会挂着，
        ///    玩家再点一下就是 <c>File.Delete</c> 一个不存在的路径（异常 + 红字）。
        /// </summary>
        private static void AfterReplayDeleted(string path)
        {
            // 详情面板若正展开这一份，必须收起——它读的文件已经不在了，
            // 留着会让下一帧去读一个不存在的路径（每次都失败、每次都记日志）。
            ReplayInfoPanel.Forget(path);
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PlayActions.UnloadReplay();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[SpectreReplay] 删除后卸载回放失败：" + ex.Message);
                }
                _loadedPath = "";
            }
            _replayCache = null;
            _replayCacheTick = int.MinValue;
        }

        private static void DrawPlaybackState()
        {
            if (is_playing)
            {
                string song = "?";
                if (data != null && data.SongName is string sn
                    && !string.IsNullOrEmpty(sn))
                {
                    song = sn;
                }
                GUILayout.Label(Loc.F("playback.armed", song), Gui.LabelStyle);
                // 这条提示只有在关卡加载后（Start_Rewind）才算得准，见 DrawFullRunBadge。
                DrawFullRunBadge();
                GUILayout.BeginHorizontal();
                string stopLabel = Loc.T("playback.stop");
                if (GUILayout.Button(stopLabel, Gui.ButtonStyle,
                        GUILayout.Width(Gui.ButtonW(stopLabel, Gui.ButtonStyle, 140f)),
                        GUILayout.Height(26f)))
                {
                    PlayActions.StopPlaying();
                }
                // 「停止」与「卸载」是两件事：停止只是不再注入按键，回放仍装着；
                // 卸载才是完全退回未装弹状态。
                DrawUnloadButton();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                // 键盘音整块（导出 + 这一份的音量/静音/偏移）只在回放真的带
                // 键盘音时才出现：没有声音可调可导，画出来是一排死控件。
                if (HasKeybdSound)
                {
                    DrawExportKeybdButton();
                    // 音量 / 静音 / 偏移紧跟在导出按钮下面：导出与调音操作的是
                    // 同一份回放，玩家调完往往就想导出或存回去，不必去别处找。
                    // 两个状态分支各挂一次——与 DrawExportKeybdButton 同一个理由。
                    DrawKeybdPrefsControls();
                }
                return;
            }
            if (is_recording)
            {
                GUILayout.Label(Loc.T("status.recording"), Gui.LabelStyle);
                // 停止录制，不是取消保存。CancelSave 会丢掉本局待存数据，两者必须分开。
                string recStopLabel = Loc.T("record.stop");
                if (GUILayout.Button(recStopLabel, Gui.ButtonStyle,
                        GUILayout.Width(Gui.ButtonW(recStopLabel, Gui.ButtonStyle, 140f)),
                        GUILayout.Height(26f)))
                {
                    PlayActions.StopRecording();
                }
                return;
            }
            // **"还没载入"只在真的什么都没装时才说。**
            // 这句原先无条件打印，而它上面的两个分支只覆盖「播放中」「录制中」——
            // 于是"已装弹、还没开播"这个状态走进了这里，同一块里先出现
            // 「还没载入回放」，紧接着 DrawArmedHint 又说「已装好：某某」，
            // 两句话当场打架，玩家分不清到底装没装上。
            if (PlayMode && data != null)
            {
                // 顺序：先报状态，再给操作。反过来会让「卸载回放」按钮压在
                // 「已装好：某某」上面，读起来像是给上面那句 idle 配的操作。
                DrawArmedHint();
                // 已装弹但还没开播时 data 仍在，玩家需要能反悔——此时上面那个
                // is_playing 分支走不到，不补这一处就会出现"刚载入就又卸不掉了"。
                GUILayout.BeginHorizontal();
                DrawUnloadButton();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                if (HasKeybdSound)
                {
                    DrawExportKeybdButton();
                    DrawKeybdPrefsControls();
                }
                DrawTimingSummary();
                DrawOpenLevelRow();
                return;
            }

            if (Gui.ModernMode)
            {
                Rect r = GUILayoutUtility.GetRect(1f, 44f, GUILayout.ExpandWidth(true),
                    GUILayout.Height(44f));
                GUI.Label(new Rect(r.x + Gui.ModernRowPadX, r.y, r.width - Gui.ModernRowPadX * 2f,
                    r.height), Loc.T("playback.idle"), Gui.ModernDescStyle);
            }
            else
            {
                GUILayout.Label(Loc.T("playback.idle"), Gui.NoteStyle, GUILayout.ExpandWidth(true));
            }
        }

        /// <summary>
        /// 「卸载回放」按钮。**两个状态分支都要给**（回放中 / 已装弹未开播），
        /// 因为它们的宿主分支不同，只补一处就会剩下另一个状态卸不掉。
        ///
        /// 用 <see cref="Gui.QuietButtonStyle"/> 而不是普通按钮：它是"撤销装弹"
        /// 这类破坏性小操作，不该和「回放」那颗主行动按钮抢视觉权重。
        /// 二次确认也没必要——卸载不删文件，重新载入即可。
        /// </summary>
        private static void DrawUnloadButton()
        {
            string label = Loc.T("playback.unload");
            if (GUILayout.Button(label, Gui.QuietButtonStyle,
                    GUILayout.Width(Gui.ButtonW(label, Gui.QuietButtonStyle, 110f)),
                    GUILayout.Height(26f)))
            {
                try
                {
                    PlayActions.UnloadReplay();
                    _loadedPath = "";
                    _loadedCloudName = "";
                }
                catch (Exception ex)
                {
                    // 卸载失败不该把整个设置页带走（与载入侧同一处理）
                    Debug.LogWarning("[SpectreReplay] 卸载回放失败：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 「导出键盘音」按钮 + 一句自我说明。
        ///
        /// <para><b>为什么两个状态分支都要挂。</b>正在播放与「已装弹未开播」
        /// 各走各的 return，只补一处就会剩下另一个状态下导不出来——与
        /// <see cref="DrawUnloadButton"/> 同一个理由。</para>
        ///
        /// <para><b>为什么 enabled 只看 data。</b>导出的是「当前装载的回放」，
        /// 与是否正在播放无关；而 SpectreState 里那个键音标志在键盘音嵌进
        /// 回放文件之后语义已经不等同于「这份有没有键盘音」，拿它当开关会把
        /// 其实能导出的情况一起灰掉。</para>
        ///
        /// <para>走 <see cref="OnGui.Button"/> 而不是自绘按钮：禁用时「只画不响应」
        /// 它已经统一处理过（GUI.Button 是内建控件，外层 Gui.Dim 拦不住点击）。
        /// 代价是它不返回 Rect，悬停提示给不出来，所以把「导出成什么」写成
        /// 按钮下面的一句说明。</para>
        /// </summary>
        private static void DrawExportKeybdButton()
        {
            OnGui.Button("playback.keybd.export", ExportKeybdSound, false, data != null);
            OnGui.Note("playback.keybd.export.tip");
        }

        /// <summary>
        /// 「这一份回放自己的键盘音」三行控件 + 两颗按钮：
        /// 音量 / 静音 / 时间偏移 / 恢复全局 / 保存到回放文件。
        ///
        /// <para><b>为什么是「这一份」而不是全局设置。</b>同一段键盘音在不同的
        /// 机器、不同的谱面偏移下对轴误差不一样，而玩家往往只想修正在听的这一份；
        /// 覆盖值写进回放文件后跟着文件走（上传云、发给别人都还在），全局设置
        /// 只能修自己这台机器。</para>
        ///
        /// <para><b>为什么滑条直接读写 <c>data.KeybdPrefs</c>、没有本地草稿。</b>
        /// 播放侧（Patch_Playback）每帧现读这份数据，所以拖动即时能听见；
        /// 存草稿再回写反而要多一次同步，且播放中容易与播放侧不同步。
        /// 「保存」是显式动作，只负责把当前值写进文件——两者职责不重叠。</para>
        ///
        /// <para><b>为什么没设过覆盖值时显示全局值。</b>此时实际发声的就是全局
        /// 音量 / 偏移（播放侧同样是 this-or-global），滑条停在 0 会与耳边听到的
        /// 声音矛盾；显示当前生效值，玩家一拖才切成这一份的覆盖值。</para>
        ///
        /// <para><b>为什么整块都要判 <c>data != null</c>。</b>这些值存在某一份
        /// 具体回放的数据里，没装载回放时没有任何可写目标；画出来只会是一排
        /// 点了没反应的控件。</para>
        /// </summary>
        private static void DrawKeybdPrefsControls()
        {
            if (data == null)
            {
                return;
            }
            Gui.Divider();
            GUILayout.Label(Loc.T("playback.keybd.section"), Gui.BoldStyle, GUILayout.ExpandWidth(true));

            // 每行现读一次，不用开头那份快照：同一帧里上面一行刚改了数据，
            // 下面几行若还读旧快照就会显示上一帧的状态（拖音量后静音行闪一下）。
            // 初值一律先夹进量程：这个数来自**文件**（别的版本写的、或玩家手改的），
            // 超范围的 Volume=99 会让滑块的旋钮算到轨道外面（k 按 (v-min)/(max-min)
            // 算），再拖一下数值就从那个越界位置往回跳。夹的只是**显示值**，
            // 不回写文件——玩家没动过就不该被悄悄改掉。
            KeybdPrefs vol = data.KeybdPrefs;
            // 滑条量程就是 0..100（百分比），内部只存 0..10 档 —— 两端折算一次，
            // 右侧那个**可编辑**的数值框才不会被玩家当成百分比输错（框里的数
            // 与显示的单位必须同量纲，否则输入 "50" 会被夹成满音量）。
            OnGui.RowSlider("playback.keybd.volume",
                (vol.HasVolume ? Mathf.Clamp(vol.Volume, 0, 10) : Mathf.Clamp(KeybdSoundVolume, 0, 10)) * 10f,
                0f, 100f,
                v => ((int)v) + " %", v =>
                {
                    KeybdPrefs p = data.KeybdPrefs;
                    // 拖动即视为「这一份自己说了算」：只改 Volume 不置 HasVolume
                    // 的话，播放侧仍读全局值，滑条动了却听不出变化。
                    p.HasVolume = true;
                    p.Volume = Mathf.Clamp((int)Mathf.Round(v / 10f), 0, 10);
                    data.KeybdPrefs = p;
                });

            OnGui.Row("playback.keybd.mute", data.KeybdPrefs.IsMuted, v =>
            {
                KeybdPrefs p = data.KeybdPrefs;
                p.IsMuted = v;
                data.KeybdPrefs = p;
            });

            KeybdPrefs off = data.KeybdPrefs;
            OnGui.RowSlider("playback.keybd.offset",
                off.HasOffset ? Mathf.Clamp(off.OffsetMs, -500, 500) : Mathf.Clamp(MicrophoneOffset, -500, 500),
                -500f, 500f,
                // 正负号必须显式写出来：只显示 "120" 时玩家分不清是提前还是
                // 推后，而偏移的方向感正是这个控件唯一的信息。
                v => (v >= 0f ? "+" : "") + ((int)v) + " ms", v =>
                {
                    KeybdPrefs p = data.KeybdPrefs;
                    p.HasOffset = true;
                    p.OffsetMs = Mathf.Clamp((int)v, -500, 500);
                    data.KeybdPrefs = p;
                });

            OnGui.Button("playback.keybd.reset", () =>
            {
                // 整段置 default，而不是逐字段清零：三个 Has 全归 false 才是
                // 「跟随全局」。逐个清数值会留下 Has=true + 值 0 的覆盖，
                // 玩家点完「恢复全局」反而变成静音，比不点还糟。
                data.KeybdPrefs = default;
            }, false);

            // 云装载的那一份整条链路不落盘（见 _loadedCloudName 的说明），
            // 没有本地路径就没有可重写的文件；按钮必须灰掉并解释一句，
            // 否则玩家只会觉得这颗按钮坏了。
            bool hasLocal = !string.IsNullOrEmpty(_loadedPath);
            OnGui.Button("playback.keybd.save", SaveKeybdPrefs, true, hasLocal);
            OnGui.Note("playback.keybd.note");
            if (!hasLocal)
            {
                OnGui.Note("playback.keybd.savenolocal");
            }
        }

        /// <summary>
        /// 「保存到回放文件」：把当前覆盖值重写进这份回放的本地文件。
        ///
        /// <para><b>为什么只认 <c>_loadedPath</c>。</b>它才是这份回放的真实本地
        /// 路径；云端装载那份没有它，写了也没地方落。路径失效（文件被移走 /
        /// 删掉）由 <see cref="ReplayManager.TrySaveKeybdPrefs"/> 自己判并回
        /// <c>not_found</c>，这里不重复查一遍文件。</para>
        ///
        /// <para><b>为什么成功提示里带路径。</b>重写是原地覆盖、界面上没有任何
        /// 可见变化（文件大小几乎不变），不报一句的话玩家无法确认到底存没存。</para>
        ///
        /// <para><b>异常必须在这里兜住。</b>这段跑在 OnGUI 里，抛出去会被 UMM
        /// 当成整块面板出错收掉（与导出 / 载入侧同一处理）。</para>
        /// </summary>
        private static void SaveKeybdPrefs()
        {
            try
            {
                if (ReplayManager.TrySaveKeybdPrefs(data, _loadedPath, out string err))
                {
                    Status.Say("playback.keybd.saved", _loadedPath);
                    return;
                }
                Status.Warn("playback.keybd.savefail", KeybdSaveErrorText(err));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 保存回放键盘音设置失败：" + ex);
                Status.Warn("playback.keybd.savefail", Loc.T("playback.keybd.writefail"));
            }
        }

        /// <summary>
        /// 保存键盘音设置的错误代号 → 给玩家看的一句话。
        ///
        /// <para><b>为什么不能把代号直接显示。</b>那是内部约定（no_local_file /
        /// not_found / write_failed），玩家看到只会是一串英文代号，还顺带把
        /// 实现细节露在界面上。</para>
        ///
        /// <para>认不出的代号一律落到「写入文件失败」：调用确实失败了，而写失败
        /// 是最可能、也最可操作的一种（存档目录不可写 / 磁盘满）。</para>
        /// </summary>
        private static string KeybdSaveErrorText(string token)
        {
            switch (token)
            {
                case "no_local_file":
                    // 理论上到不了（无本地路径时保存按钮是灰的），真到了也照实说：
                    // 这种情况下界面上确实没有可写的文件。
                    return Loc.T("playback.keybd.savenolocal");
                case "not_found":
                    return Loc.T("playback.keybd.savenofile");
                default:
                    // 含 no_replay（未装载）与 write_failed：两者对玩家而言都是
                    // 「这次没写进去」，归到同一句里，别让界面出现 5 种说法。
                    return Loc.T("playback.keybd.writefail");
            }
        }

        /// <summary>
        /// 把当前装载回放的键盘音导出成一个 wav。
        ///
        /// <para><b>异常必须在这里兜住。</b>这段跑在 OnGUI 里，抛出去会被 UMM
        /// 当成整块面板出错收掉（与载入 / 卸载侧同一处理）。</para>
        ///
        /// <para><b>失败代号要先翻译。</b>把 no_sound 这类内部代号直接塞进提示，
        /// 玩家看到的是一行看不懂的英文代号，也把实现细节露在界面上。</para>
        /// </summary>
        private static void ExportKeybdSound()
        {
            try
            {
                if (Features.Replay.ReplayManager.ExportKeybdSound(
                        data, out string savedPath, out string err))
                {
                    Status.Say("playback.keybd.export.ok", savedPath);
                    return;
                }
                Status.Warn("playback.keybd.export.fail", KeybdExportErrorText(err));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SpectreReplay] 导出键盘音失败：" + ex);
                Status.Warn("playback.keybd.export.fail",
                    Loc.T("playback.keybd.export.writefail"));
            }
        }

        /// <summary>
        /// 键盘音导出的错误代号 → 给玩家看的一句话。
        ///
        /// <para><b>为什么不复用 <see cref="CloudErrorText"/>。</b>它把 no_dir
        /// 归进「Steam 云端不可用」，而这条路上根本没有云端参与；导出失败的原因
        /// 必须说得具体（没键盘音 / 目录坏了 / 数据坏了 / 写不进去），否则玩家
        /// 只能对着同一颗按钮反复点。</para>
        ///
        /// <para>no_replay 理论上到不了（没装载回放时按钮是灰的），真到了也按
        /// 「没有键盘音」说——那种情况下界面上确实没有任何可导出的东西。</para>
        ///
        /// <para>认不出的代号一律落到「写入文件失败」：调用确实失败了，而写失败
        /// 是最可能、也最可操作的一种（换个保存目录再试）。</para>
        /// </summary>
        private static string KeybdExportErrorText(string token)
        {
            switch (token)
            {
                case "no_sound":
                case "no_replay":
                    return Loc.T("playback.keybd.export.none");
                case "no_dir":
                    return Loc.T("playback.keybd.export.nodir");
                case "decode_failed":
                    return Loc.T("playback.keybd.export.decodefail");
                default:
                    return Loc.T("playback.keybd.export.writefail");
            }
        }

        /// <summary>
        /// 「打开这一关」按钮。从已载入回放里读 <c>level_path</c>，一键跳到它录自的那一关。
        ///
        /// **为什么需要它。** 回放只在"重开/换关/进关"时才启动（见
        /// <see cref="OpenReplayLevel"/> 的说明），而练习谱/自制谱要自己找回去
        /// 很麻烦——玩家录完一局、退出、回放页点载入，然后只能靠记忆翻关卡列表。
        /// 这颗按钮就是那条回去的路。
        ///
        /// **只对"已装弹的回放"显示**（<c>PlayMode &amp;&amp; data != null</c>）：
        /// 录制模式下 data 也在记录，不加这个门槛的话自己打一局也会冒出这颗按钮。
        /// </summary>
        private static void DrawOpenLevelRow()
        {
            if (!PlayMode || data == null)
            {
                return;
            }
            if (data.LevelPath is not string levelPath
                || string.IsNullOrEmpty(levelPath))
            {
                return;
            }

            GUILayout.Space(4f);
            // 官方关卡没有谱面文件，关卡数据烤在游戏里，靠 GCS.internalLevelName
            // 选（形如 "0-0"/"AR-X"）。录制时已把名字存进 data.InternalLevelName。
            bool official = levelPath.StartsWith("OfficialLevel", StringComparison.Ordinal)
                || (data.IsOfficialLevel is bool iso && iso);
            if (official)
            {
                string internalName = data.InternalLevelName is string nm ? nm : "";
                // 老回放没有内部关卡名：给提示而不是一颗点了没反应的按钮。
                if (string.IsNullOrEmpty(internalName))
                {
                    GUILayout.Label(Loc.T("playback.openlevel.official")
                        + (string.IsNullOrEmpty(levelPath) ? "" : levelPath),
                        Gui.NoteStyle, GUILayout.ExpandWidth(true));
                    return;
                }
                // 标题行 + 直达按钮：歌名与关卡 id 都写出来，玩家才知道要跳去哪。
                GUILayout.Label(Loc.F("playback.openlevel.official_go", internalName),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
                string goOfficial = Loc.T("playback.openlevel.official.go");
                if (GUILayout.Button(goOfficial,
                        Gui.ButtonStyle,
                        GUILayout.Width(Gui.ButtonW(goOfficial, Gui.ButtonStyle, 160f)),
                        GUILayout.Height(24f)))
                {
                    try
                    {
                        OpenReplayLevel.GoOfficial();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[SpectreReplay] 打开官方关卡失败：" + ex.Message);
                        Status.Warn("playback.openlevel.busy");
                    }
                }
                return;
            }
            if (!File.Exists(levelPath))
            {
                GUILayout.Label(Loc.T("playback.openlevel.missing") + ClipMiddle(levelPath, 46),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
                return;
            }

            // 录自编辑器就回编辑器、录自关卡就回关卡——两边起跳逻辑不同
            string recScene = data.SceneName is string sc ? sc : "";
            bool editorReplay = recScene == "scnEditor";
            string captured = levelPath;
            string openLabel = Loc.T(editorReplay ? "playback.openlevel.editor" : "playback.openlevel");
            if (GUILayout.Button(openLabel,
                    Gui.ButtonStyle,
                    GUILayout.Width(Gui.ButtonW(openLabel, Gui.ButtonStyle, 160f)),
                    GUILayout.Height(24f)))
            {
                try
                {
                    OpenReplayLevel.Go(captured, editorReplay);
                }
                catch (Exception ex)
                {
                    // 跳转失败（谱面损坏/场景状态异常）不该把整个设置页带走
                    Debug.LogWarning("[SpectreReplay] 打开关卡失败：" + ex.Message);
                    Status.Warn("playback.openlevel.busy");
                }
            }
        }

        private static void DrawArmedHint()
        {
            if (!PlayMode || data == null)
                return;
            string song = data.SongName is string sn && !string.IsNullOrEmpty(sn)
                ? sn : "?";
            GUILayout.Space(2f);
            GUILayout.Label(Loc.F("playback.ready", song), Gui.NoteStyle, GUILayout.ExpandWidth(true));
            DrawFullRunBadge();
        }

        /// <summary>
        /// 重算「全量回放」标志。判定 = 从第 0 块起、终点是本关最后一块：
        /// 起点与终点来自回放文件，**本关总块数只能问当前关卡**。
        ///
        /// <para><b>为什么每次绘制都重算，而不是只信启动时那一次赋值。</b>
        /// 这个标志依赖 <c>ADOBase.lm.listFloors</c>，而它只在关卡加载后存在：
        /// 从设置页「打开关卡」进关的路径上，装弹那一刻 <c>lm</c> 还是 null，
        /// 算不出来；<see cref="Features.Replay.Patch_Lifecycle"/> 里那次赋值又排在
        /// 一长串校验之后，任一校验提前返回就漏掉了。所以绘制时按活数据再算一次
        /// ——只要关卡在，结论就一定正确。</para>
        ///
        /// <para>关卡没加载（<c>lm</c> 为 null 或还没有地板）时**保持原值**：
        /// 此时算不出来，硬判 false 会把已经算对的结论擦掉。</para>
        /// </summary>
        private static void RefreshFullRun()
        {
            if (data == null || !data.StartTile.HasValue || !data.EndTile.HasValue)
            {
                IsFullRun = false;
                return;
            }
            if (data.StartTile != 0)
            {
                IsFullRun = false;
                return;
            }
            int total = 0;
            try
            {
                if (ADOBase.lm != null && ADOBase.lm.listFloors != null)
                    total = ADOBase.lm.listFloors.Count;
            }
            catch
            {
                total = 0;
            }
            if (total <= 0)
                return; // 关卡还没加载：算不出来，别把已有结论擦掉
            IsFullRun = data.EndTile == total - 1;
        }

        private static void DrawFullRunBadge()
        {
            // 每次画的时候再算一次：这个标志依赖关卡数据，而关卡数据在
            // 「先装弹、后进关」的流程里，装弹那一刻还不存在。
            RefreshFullRun();
            if (!IsFullRun)
                return;
            Color prev = GUI.contentColor;
            GUI.contentColor = Gui.OkCol;
            GUILayout.Label(Loc.T("playback.fullrun"), Gui.LabelStyle, GUILayout.ExpandWidth(true));
            GUI.contentColor = prev;
        }

        /// <summary>
        /// 已载入回放的命中误差摘要。
        ///
        /// **为什么要显示它。** ReplayAnalyzer.Analyze 是纯计算、不碰引擎，
        /// 但**必须有调用点**才算存在 —— 那一整块"整体偏早/偏晚、中位数、
        /// 标准差"是玩家打完一局最想知道的事，没人调用就等于不存在。
        ///
        /// 只在**已载入回放且关卡已加载**时显示：Analyze 的返回 null 带有
        /// need_level（没进关）/ no_data（没有可分析的命中）两种本地化键后缀，
        /// 都不该在这里刷成一行错误信息——回放页没进关是常态。
        /// </summary>
        private static void DrawTimingSummary()
        {
            if (data == null)
                return;
            string reason;
            ReplayAnalyzer.Report rep;
            try
            {
                rep = ReplayAnalyzer.Analyze(data, out reason);
            }
            catch (Exception ex)
            {
                // 分析纯属附加信息，任何异常都不该把整个设置页带走
                // （OnGUI 抛异常 = UMM 收掉整个面板）
                Debug.LogWarning("[SpectreReplay] 命中误差分析失败：" + ex.Message);
                return;
            }
            if (rep == null)
                return;

            GUILayout.Space(4f);
            Gui.Divider();
            GUILayout.Label(Loc.T("playback.timing"), Gui.LabelStyle);
            GUILayout.Label(Loc.F("playback.timing_summary",
                rep.AnalyzedHits, rep.EarlyCount, rep.LateCount, SignMs(rep.MedianMs)),
                Gui.NoteStyle, GUILayout.ExpandWidth(true));
            GUILayout.Label(Loc.F("playback.timing_detail",
                SignMs(rep.MeanMs), rep.SigmaMs.ToString("0.0") + "ms",
                SignMs(rep.MinMs), SignMs(rep.MaxMs)),
                Gui.NoteStyle, GUILayout.ExpandWidth(true));
            int skipped = rep.TotalHits - rep.AnalyzedHits;
            if (skipped > 0)
            {
                GUILayout.Label(Loc.F("playback.timing_skipped", skipped),
                    Gui.NoteStyle, GUILayout.ExpandWidth(true));
            }

            // ── 命中分布图 ──────────────────────────────────────────
            // 上游 Spectre 是"点分析按钮才画"（SpectreUiPages.More.cs:123-156）。
            // 我们这里**不需要按钮**：摘要已经是自动算的（Analyze 在
            // DrawTimingSummary 开头就调过），图只是把同一份 Report 画出来，
            // 再加一颗按钮等于让玩家多点一下才看到已有的数据。
            // （原计划的"手动分析按钮"因此取消——见 Loc 里未使用的
            //  playback.analyze* 三个键，它们是为那个方案准备的，现已不需要。）
            GUILayout.Space(4f);
            GUILayout.Label(Loc.T("playback.analyze.hint"),
                Gui.NoteStyle, GUILayout.ExpandWidth(true));
            DrawScatter(rep);
        }

        /// <summary>
        /// 画命中分布柱状图。贴图由 <see cref="HitScatter"/> 缓存并按需重建；
        /// 这里只负责"取一张矩形、把贴图贴上去"。
        ///
        /// 用 <c>GUI.DrawTexture</c> 而不是 GUILayout.Box/Image：IMGUI 里画贴图
        /// 只有这一条路（上游那套 uGUI 的 RawImage 在 IMGUI 页面里不存在）。
        /// </summary>
        private static void DrawScatter(ReplayAnalyzer.Report rep)
        {
            try
            {
                Texture2D tex = HitScatter.Get(rep, data);
                if (tex == null)
                {
                    GUILayout.Label(Loc.T("playback.analyze.none"),
                        Gui.NoteStyle, GUILayout.ExpandWidth(true));
                    return;
                }
                // 高度固定 120（与贴图 H 一致，1:1 不缩放，柱子不会被拉糊）
                Rect r = GUILayoutUtility.GetRect(1f, 120f, GUILayout.ExpandWidth(true));
                Color old = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(r, tex, ScaleMode.StretchToFill);
                GUI.color = old;
                Gui.Frame(r, Gui.LineCol);
            }
            catch (Exception ex)
            {
                // 画图纯属附加信息，任何异常都不该把整个设置页带走
                Debug.LogWarning("[SpectreReplay] 命中分布图绘制失败：" + ex.Message);
            }
        }

        /// <summary>毫秒值统一带符号：-1.2ms / +0.8ms。纯 ASCII 与数字，不进文案表。</summary>
        private static string SignMs(float ms)
            => (ms >= 0f ? "+" : "") + ms.ToString("0.0") + "ms";

    }
}
