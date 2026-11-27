using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SpectreReplay.API;
using SpectreReplay.Features.Cloud;
using SpectreReplay.Features.Compat;
using SpectreReplay.Features.Replay;
using HarmonyLib;
using MonsterLove.StateMachine;
using SkyHook;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityModManagerNet;
using Object = UnityEngine.Object;

namespace SpectreReplay;

public static class SpectreState
{
    internal static class DataIntegrity
    {
        /// <summary>
        /// 回放数据缺字段就返回 true（调用点在 Patch_Lifecycle）。
        /// 必需字段清单见 <see cref="ReplayData.FirstMissingRequiredKey"/>。
        /// </summary>
        internal static bool Check()
        {
            string missing = data?.FirstMissingRequiredKey;
            if (missing == null) return false;
            Debug.Log("Missing:" + missing);
            return true;
        }
    }

    /// <summary>
    /// 从已载入的回放跳到它录自的那一关（含编辑器）。
    ///
    /// **为什么必须有它。** 回放的启动时机是 <c>scrController.Start_Rewind</c>
    /// ——也就是"重开/换关/进关"时。玩家在设置页点「回放」只是**装弹**，
    /// 必须自己再进那一关才会播。而练习谱/自制谱要找到那一关很麻烦
    /// （退出当前关 → 关卡列表里翻 → 记不清是哪个文件）。
    /// 上游 Spectre 给了「打开关卡」按钮直达，本模组的对应实现就在下面。
    ///
    /// **转场状态设置必须原样**——
    /// 这不是可以随手简化的代码：漏设 <c>previousScene</c> 会让"返回"按钮
    /// 跳错地方，漏设 <c>checkpointNum</c> 会从第 0 块而不是回放起点开始。
    /// </summary>
    internal static class OpenReplayLevel
    {
        /// <summary>
        /// 跳到回放录自的关卡。<paramref name="editorReplay"/> 为真时走编辑器。
        ///
        /// 调用方负责先确认 <c>levelPath</c> 非空且文件存在——这里只做跳转，
        /// 不替调用方判断该不该跳（那属于界面层的决策）。
        /// </summary>
        internal static void Go(string levelPath, bool editorReplay)
        {
            if (string.IsNullOrEmpty(levelPath))
            {
                return;
            }
            // 跳转前必须停掉正在跑的录制/回放：否则旧场景的协程会带着
            // 旧状态跨场景活下来，在新关卡里继续注入按键。
            PlayActions.StopPlaying();
            PlayActions.StopRecording();

            int startTile = data != null && data.StartTile is int stv
                ? Math.Max(0, stv) : 0;

            if (editorReplay)
            {
                // 编辑器：让编辑器加载完自己打开这个谱面，并把选中砖定位到起点。
                // SelectFloor 是异步的（谱面加载要几帧），所以用协程等就绪。
                scnEditor.levelToOpenOnLoad = levelPath;
                GCS.sceneToLoad = "scnEditor";
                GCS.checkpointNum = startTile;
                // 只从非关卡场景进来时才清掉试玩/练习标记——上游原样保留这个判断：
                // 从关卡内跳编辑器时清了会丢当前试玩设置。
                if (!ADOBase.isScnGame)
                {
                    GCS.speedTrialMode = false;
                    GCS.practiceMode = false;
                }
                ADOBase.loader.LoadSceneWithTransition(WipeDirection.StartsFromRight);
                MainThread.StartCoroutine(SelectEditorFloorWhenReady(startTile));
            }
            else
            {
                GCS.checkpointNum = startTile;
                GCS.previousScene = "scnGame";
                if (data != null && data.SpeedTrailMode is bool stm)
                {
                    GCS.speedTrialMode = stm;
                    if (stm && data.SpeedTrail is double st)
                    {
                        GCS.currentSpeedTrial = (float)st;
                    }
                }
                scrController controller = scrController.instance;
                if (controller != null)
                {
                    // 已经在关卡里：直接换谱面，不重载场景（更快，也不闪白）
                    controller.LoadCustomLevel(levelPath);
                }
                else
                {
                    GCS.sceneToLoad = "scnGame";
                    GCS.customLevelPaths = new[] { levelPath };
                    GCS.loadCustomFromBundle = false;
                    scrController.deaths = 0;
                    ADOBase.loader.LoadSceneWithTransition(WipeDirection.StartsFromRight);
                }
            }
            Debug.Log("[SpectreReplay] open replay level (" + (editorReplay ? "editor" : "game")
                + "): " + levelPath);
        }

        /// <summary>
        /// 跳到回放录自的**官方关卡**。
        ///
        /// 官方关卡没有 .adofai 文件，关卡数据烤在游戏里，靠
        /// <c>GCS.internalLevelName</c>（形如 "0-0"、"AR-X"）选，不靠文件路径。
        ///
        /// **游戏自己的加载链**（反编译 Assembly-CSharp 核实）：
        ///  ① <c>GCS.internalLevelName</c> 非 null ⇒ <c>ADOBase.isInternalLevel</c> 为真；
        ///  ② <c>scnGame</c> 场景重载后，<c>scnGame.Update</c> 在第 3 帧做
        ///     「<c>text = GCS.internalLevelName ?? GCS.customLevelPaths[index]</c>」→
        ///     <c>LoadAndPlayLevel(text)</c>；
        ///  ③ <c>scnGame.Awake</c> 只在 <c>internalLevelName</c> 为 null **且**
        ///     customLevelPaths 为空时才去填默认值——所以只要我们**先**把名字写好，
        ///     它就不会被覆盖。
        ///  ⇒ 我们只需要：清掉 customLevelPaths、写好 internalLevelName、重载 scnGame。
        ///
        /// **必须清 <c>customLevelPaths</c>**：它是 `scnGame.Update` 那个 null 合并的
        /// 另一边，留着上一关的自制谱路径会让关卡选错。
        ///
        /// **关卡名从哪来**：回放录制时 <c>Patch_Lifecycle</c> 就把
        /// <c>GCS.internalLevelName</c> 存进了 <c>data.InternalLevelName</c>
        /// （官方关卡才有值，自制谱是空串）。所以这里**不需要**从 LevelPath 里
        /// 反解——那个字段对官方关卡是 <c>"OfficialLevel &lt;歌名&gt;"</c>，不是关卡 id。
        /// </summary>
        internal static void GoOfficial()
        {
            if (data == null)
            {
                return;
            }
            if (data.InternalLevelName is not string internalName
                || string.IsNullOrEmpty(internalName))
            {
                // 老回放（该字段还没加时录的）没有内部关卡名，跳不了。
                // 这**不是**缺陷，是数据缺失，说清楚就行。
                // **必须走 Ui.Loc，不是 LocalizationManager**：这条文案只存在于
                // 界面层那两张表里（zh/en），14 语言大表里没有它——用错会在
                // LocCheck 那里报"缺 14 种语言"，而运行时则是露出原始键名。
                TriggerMessage(Ui.Loc.T("playback.openlevel.official"), extra: 1f);
                Debug.LogWarning("[SpectreReplay] 该回放没有记录内部关卡名（旧版本录制），无法直达官方关卡");
                return;
            }

            // 与 Go() 同理：跳转前必须停掉录制/回放，否则旧场景的协程会带着
            // 旧状态跨场景活下来，在新关卡里继续注入按键。
            PlayActions.StopPlaying();
            PlayActions.StopRecording();

            int startTile = data.StartTile is int stv
                ? Math.Max(0, stv) : 0;

            GCS.checkpointNum = startTile;
            GCS.previousScene = "scnGame";
            // 官方关卡：**清掉**自制谱路径，只留 internalLevelName 这一条路。
            // 这两个字段是 scnGame.Update 里二选一的两边，同时有值会打架。
            GCS.customLevelPaths = null;
            GCS.loadCustomFromBundle = false;
            GCS.internalLevelName = internalName;
            if (data.SpeedTrailMode is bool stm)
            {
                GCS.speedTrialMode = stm;
                if (stm && data.SpeedTrail is double st)
                {
                    GCS.currentSpeedTrial = (float)st;
                }
            }
            GCS.sceneToLoad = "scnGame";
            scrController.deaths = 0;
            ADOBase.loader.LoadSceneWithTransition(WipeDirection.StartsFromRight);
            Debug.Log("[SpectreReplay] open replay official level: " + internalName
                + " (startTile=" + startTile + ")");
        }

        private static System.Collections.IEnumerator SelectEditorFloorWhenReady(int floor)
        {
            for (int i = 0; i < 1800; i++)
            {
                scnEditor editor = ADOBase.editor;
                if (editor != null && !editor.isLoading
                    && editor.floors != null && editor.floors.Count > floor)
                {
                    // cameraJump 必须 true：这是交互式定位，镜头要跟到起始砖。
                    // （视频渲染里用的是 false——那是离线渲染，镜头由回放接管，
                    //  两种场景的取舍不同，不能照搬。）
                    editor.SelectFloor(editor.floors[floor], cameraJump: true);
                    yield break;
                }
                yield return null;
            }
            Debug.Log("[SpectreReplay] 编辑器定位起始砖超时（第 " + floor + " 块）");
        }
    }

    internal class PlayActions
    {
        internal static void SwitchToPlayMode()
        {
            // Do not clear the flags directly: Stop owns the restoration, key releases,
            // recording save, and public stop events for the old session.
            StopPlaying();
            StopRecording();
            PlayMode = true;
            RecordMode = false;
            HitLocked = false;
            PendingSave = false;
            LateSaveMode = false;
            PlayIndex = 0;
            KeyEventIndex = 0;
            IgnoreMarkFail = false;
        }

        internal static void SwitchToRecordMode()
        {
            StopPlaying();
            StopRecording();
            HitLocked = false;
            PlayMode = false;
            RecordMode = true;
            PendingSave = false;
            LateSaveMode = false;
            PlayIndex = 0;
            KeyEventIndex = 0;
            IgnoreMarkFail = false;
            IsFullRun = false;
        }

        /// <summary>
        /// 卸载当前回放，回到未装弹的录制态。
        ///
        /// 顺序不能改（照上游 <c>SpectreUiPages.More.cs:40-44</c>）：
        /// ① 先 StopPlaying/StopRecording——回放/录制协程还在跑时清 data，
        ///    它们下一帧会去读已清空的 data，轻则报错重则卡死；
        /// ② 再 UnLoadReplay 换出 data 并清掉键盘音（<c>WavLoader.loaded_clip</c>
        ///    与 <c>HasKeybdSound</c> 不清的话，下一局会带着上一局的键盘音）；
        /// ③ 最后 SwitchToRecordMode 把 PlayMode 翻回 false 并复位所有游标。
        /// </summary>
        internal static void UnloadReplay()
        {
            // 用户在捕获期间卸载/换装不是自然播完：先取消作业，再翻播放标志，
            // 避免捕获循环把 is_playing 变假误当成耗尽并报告不完整视频成功。
            // 正常成片的消费发生在 CaptureInProgress 已清零之后，不受影响。
            if (Features.VideoRender.VideoRender.CaptureInProgress)
                Features.VideoRender.VideoRender.CancelRender();
            PlayActions.StopPlaying();
            PlayActions.StopRecording();
            Features.Replay.ReplayManager.UnLoadReplay(out data);
            PlayActions.SwitchToRecordMode();
            // 命中分布图是静态缓存的贴图，判据里有"上一份 data 的引用"：
            // 卸载后不丢，下一份回放若条数与指纹恰好相同就会沿用旧图（图的注释
            // 一直写着"换回放/关页面时调用"，此前全仓库没有调用点）。
            Ui.HitScatter.Reset();
        }

        // 启停事件只在状态真正翻转时触发（Start/Stop 内部各有守卫，重复调用无害）；
        // 全部启停路径都经 PlayActions 收口，这里是对外事件的唯一挂点。
        // Start* 会把互斥的另一侧标志直接翻转（不经过对应 Stop），此处一并补发 Stopped
        internal static void StopPlaying()
        {
            bool was = is_playing;
            // Stop 内部对场景已卸做了空守卫，此处的 catch 是最后防线：
            // 即使再有意外异常，is_playing 标志已翻转，事件照常触发
            try { ReplayPlayer.Stop(); }
            catch (Exception ex) { Debug.LogException(ex); }
            if (was && !is_playing) SpectreAPI.RaisePlaybackStopped();
        }

        internal static void StartPlaying()
        {
            bool wasPlay = is_playing;
            // A mode flag is not a substitute for the recorder's Stop path: it owns
            // the pending save and must finish before playback takes the session.
            StopRecording();
            ReplayPlayer.Start();
            if (!wasPlay && is_playing)
                SpectreAPI.RaisePlaybackStarted();
        }

        internal static void StartRecording()
        {
            bool wasRec = is_recording;
            StopPlaying();
            ReplayRecorder.Start();
            if (!wasRec && is_recording)
                SpectreAPI.RaiseRecordingStarted();
        }

        internal static void StopRecording()
        {
            bool was = is_recording;
            ReplayRecorder.Stop();
            if (was && !is_recording) SpectreAPI.RaiseRecordingStopped();
        }
    }

    internal static bool ApiBlockPlaying = false;

    internal static bool is_playing = false;

    internal static bool is_recording = false;

    internal static bool? CachedNoFail;

    // 回放期临时套用的判定模式（Difficulty.Lenient/Normal/Strict）。
    // null = 本局没有接管过难度，Stop 时无需还原。
    // 玩家自己的默认难度存在 Persistence 里，这里只改本次会话的 GCS.difficulty。
    internal static Difficulty? CachedDifficulty;

    /// <summary>
    /// 把界面上的难度显示刷新成当前 <see cref="GCS.difficulty"/>。
    ///
    /// <para><b>两个场景各有一份显示，都要刷。</b>编辑器里是
    /// <c>EditorDifficultySelector</c>（<c>editorDifficultyText</c> / <c>bullseyeImage</c>），
    /// 而 <c>scnGame</c> 开局前那颗难度选择器是 <c>scrUIController</c> 上的
    /// <c>difficultyText</c> / <c>difficultyImage</c>。只刷编辑器那一份的话，
    /// 回放结束把难度还原之后，关卡选择界面上显示的还是回放时的难度——
    /// 玩家会以为自己的判定模式没还原。</para>
    ///
    /// <para><b>为什么不直接调 <c>ShowDifficultyContainer</c>。</b>它内部会走
    /// <c>UpdateDifficultyUI</c>，而那个方法在 <c>ShowNormalAndStrict</c> /
    /// <c>ShowLenientAndNormal</c> 两种 UI 模式下会把 Lenient/Strict **改写**成别的档位，
    /// 并回写 <see cref="GCS.difficulty"/>——我们刚还原的值会被它当场改掉。
    /// 所以只抄它的赋值方式刷显示，不碰状态机。</para>
    /// </summary>
    internal static void SyncDifficultyLabel()
    {
        try
        {
            // 编辑器：难度标签 + 靶心图
            var editorSelector = scnEditor.instance != null
                ? scnEditor.instance.editorDifficultySelector : null;
            if (editorSelector != null)
            {
                if (editorSelector.editorDifficultyText != null)
                    editorSelector.editorDifficultyText.text = RDString.Get("enum.Difficulty." + GCS.difficulty);
                if (editorSelector.bullseyeImage != null)
                    editorSelector.bullseyeImage.sprite = RDConstants.data.bullseyeSprites[(int)GCS.difficulty];
            }

            // scnGame：开局前那颗难度选择器（scrUIController.cs:427-428 同一套赋值）
            var ui = scrUIController.instance;
            if (ui != null)
            {
                if (ui.difficultyText != null)
                    ui.difficultyText.text = RDString.Get("enum.Difficulty." + GCS.difficulty);
                if (ui.difficultyImage != null)
                    ui.difficultyImage.sprite = RDConstants.data.bullseyeSprites[(int)GCS.difficulty];
            }
        }
        catch (System.Exception ex)
        {
            // 纯显示：刷不上不该影响回放本身
            Debug.Log("[SpectreReplay] 难度显示刷新失败: " + ex.Message);
        }
    }

    internal static void SyncEditorNoFailButton()
    {
        if (scnEditor.instance == null) return;
        var btn = scnEditor.instance.buttonNoFail;
        if (btn == null) return;
        if (data == null || !data.IfNoFail.HasValue) return;
        var img = btn.GetComponent<UnityEngine.UI.Image>();
        img.color = data.IfNoFail == true ? Color.white : new Color(0.5f, 0.5f, 0.5f, 1f);
    }

    internal static bool RecordMode = true;

    internal static bool PlayMode = false;

    internal static bool HitLocked = false;

    // 空壳保存拦截的「本局已拦过」标志：SaveFile 会被重试与多条 LateSave
    // 路径反复调用，拦一次说明一次就够（每次都弹会叠出 ×N 的 Toast）。
    // ReplayRecorder.Start 归零。
    internal static bool EmptySaveBlocked = false;

    internal static bool IgnoreMarkFail = false;

    internal static bool UseAsyncInput = false;

    internal static bool PendingSave = false;

    internal static bool LateSaveMode = false;

    internal static int[] HitMarginCounts = new int[Enum.GetValues(typeof(HitMargin)).Cast<int>().Max() + 1];

    // 仅作引用比较的哨兵（Patch_Recording 里判断是否同一块），不实例化 MonoBehaviour
    internal static scrFloor CurrentHitFloor;

    internal static ReplayData data;

    internal static int PlayIndex = 0;

    internal static int KeyEventIndex = 0;

    internal static bool IsFullRun = false;
    internal static bool HasKeybdSound = false;

    internal static HitMarginLimit StoredHitMarginLimit = (HitMarginLimit)0;

    // ── 调试开关（**一律不持久化**）────────────────────────────
    //
    // 这五个字段都不进 Configs.json、也不是设置页里的正式配置：它们改的是
    // 「回放校验 / 提示条 / 存盘格式 / 命中判定」这类一旦留在玩家机器上就会
    // 变成「我这回放怎么播不了」的状态，只该在开发期临时打开。
    //
    // 有效范围 = 本次装配：Main.OnToggle 每次都会把它们复位成 false，
    // 所以关开一次模组、重启游戏都会回到默认值。

    /// <summary>
    /// 调试构建标记：由 <see cref="Main.OnToggle"/> 按 <c>Debug.isDebugBuild</c> 赋值，
    /// **不是**玩家可改的配置。它本身不改变任何行为，只表示「这是个调试构建」。
    /// </summary>
    internal static bool DebugMode = true;

    /// <summary>
    /// 是否在关于页显示调试分区。**只能改这一行源码**：没有配置项、没有界面开关，
    /// 正式发行时玩家永远看不到它（见 <c>Pages.DrawAbout</c>）。
    /// </summary>
    internal static bool ShowDebugSection = false;

    /// <summary>跳过回放/音频校验。排查帧时序问题时用，默认关。</summary>
    internal static bool debug_skip_Verification = false;

    // 写不解密的 .psprp 而不是加密的 .sprp，便于排查“文件是不是坏了”。
    internal static bool debug_no_encryption = false;

    /// <summary>不弹提示条（录视频、截干净画面时用）。</summary>
    internal static bool debug_hide_text = false;

    /// <summary>走旧的命中判定路径。</summary>
    internal static bool debug_use_old_hit = false;

    internal static KeyCode save_button = (KeyCode)290;

    internal static KeyCode cancel_save_button = (KeyCode)291;

    internal static byte[] PrevKeyState = new byte[256];

    internal static byte[] CurrKeyState = new byte[256];

    private static readonly ushort[] ExtendedKeys = { 165, 163, 38, 40, 37, 39 };

    private static readonly ushort[] BlockedKeys = { 16, 17, 18, 27 };

    internal static int GetKeyboardState(byte[] pbKeyState)
        => KeyboardSimulation.GetKeyboardState(pbKeyState);

    internal static bool IsExtendedKey(ushort keyCode)
        => Array.IndexOf(ExtendedKeys, keyCode) >= 0;

    internal static bool IsBlockedKey(ushort keyCode)
        => Array.IndexOf(BlockedKeys, keyCode) >= 0;

    internal static float GetAngle()
        => scrController.instance == null ? float.NaN : GetAngle(scrController.instance);

    /// <summary>
    /// 用**调用方持有的那个** controller 取角度。
    ///
    /// 一定要有这个重载：回放推进循环用补丁入参 <c>__instance</c> 判定当前砖块
    /// （那里注释说明了为什么不能用静态 instance——场景卸载/重载的中间态它会指向
    /// 另一个实例），却用无参 <see cref="GetAngle()"/> 取角度。两个 controller 不是
    /// 同一个对象时，砖块取自 A、角度取自 B，而 B 的星球角度可以任意大，
    /// <c>angle &gt;= CurrAngle</c> 一路恒真——整份命中快照在一帧内被吃完。
    /// </summary>
    internal static float GetAngle(scrController controller)
    {
        if (controller == null) return float.NaN;
        if (controller.chosenPlanet == null) return float.NaN;
        float num = (float)(controller.chosenPlanet.angle - controller.chosenPlanet.targetExitAngle);
        if (controller.playerOne != null && !controller.playerOne.planetarySystem.isCW)
        {
            num *= -1f;
        }
        return num;
    }

    internal static bool SaveFile(bool without_sound = false, bool retry = false, bool notify = true,
        ReplayData savedData = null, string saveDirectory = null)
    {
        ReplayData data = savedData ?? SpectreState.data;
        saveDirectory ??= Settings.SavePath;
        if (data == null)
        {
            return true;
        }
        // **空壳拦截**：跑出过进度（EndTile 前进过）却 0 命中 = 命中前缀在录制
        // 途中被外部重写挤掉（JAPatcher 快照重写；2026-11-07 编辑器试玩实测：
        // 键盘音、指针轨迹都活着，唯独 Hit 快照整局为 0）。这种文件回放必然
        // 报「数据为空」，落盘只会制造悬案；当场复核补挂，下一局恢复。
        if (data.HitSnapshots.Count == 0 && (data.EndTile ?? 0) > (data.StartTile ?? 0))
        {
            // 拦一次就够：调用链的重试与多条 LateSave 路径会让 SaveFile 一局
            // 被调十来次，每次都复核+弹窗会叠出一串 Toast（2026-11-07 ×11 实测）。
            if (!EmptySaveBlocked)
            {
                EmptySaveBlocked = true;
                SpectrePatchHub.EnsureApplied("after-empty-record",
                    typeof(Patch_Hit), typeof(Patch_UpdateInputConsumeAsync),
                    typeof(Patch_PlayerControlUpdate), typeof(Patch_Won_Update));
                SpectrePatchHub.ForceRebuild(typeof(Patch_Hit));
                if (notify)
                    NotifySaveSkipped(LocalizationManager.GetLocalizedText("note.save_skip_nohits"),
                        "no hits recorded (Hit prefix lost mid-recording?)");
            }
            return false;
        }
        if (data.SaveOperation != null) return true;
        var owner = data;
        var snapshot = data.SnapshotForSave();
        snapshot.FileName ??= CreateFileName();
        string safeName = retry ? snapshot.FileName : CreateFileName(safe_mode: true);
        bool noEncryption = debug_no_encryption;
        bool compact = Settings.KeybdSoundCompact;
        string backupDirectory = Settings.Backup ? Path.Combine(Main.mod.Path, "backup") : null;
        var audioStop = snapshot.KeybdSoundStop;
        if (audioStop != null && audioStop.SaveQueued) return true;
        var operation = new ReplaySaveOperation(snapshot, saveDirectory, safeName,
            noEncryption, !without_sound, compact, backupDirectory);
        // 等封头期间就占好本局令牌；回调晚于 Reset 时不能覆盖下一局的作业标记。
        owner.SaveOperation = operation;
        operation.Accept(); // 等待录音封头也属于已接受的保存，退出门不能漏掉。
        bool queued = false;
        bool queueFailed = false;
        Action<string> queue = hash =>
        {
            if (queued) return;
            queued = true;
            bool audioMissing = !without_sound && audioStop != null && string.IsNullOrEmpty(hash);
            if (audioStop != null)
            {
                snapshot.KeybdSoundHash = hash;
                if (audioMissing) snapshot.KeybdSoundFileName = null;
            }
            // 独立于可卸载 MainThread 泵，已接受保存不会因关闭模组丢失完成观察。
            GameObject go = null;
            try
            {
                go = new GameObject("SpectreReplay.SaveCompletion");
                Object.DontDestroyOnLoad(go);
                var observer = go.AddComponent<SaveCompletion>();
                observer.Operation = operation;
                observer.Owner = owner;
                observer.Notify = notify;
                observer.AudioMissing = audioMissing;
                operation.Start();
            }
            catch (Exception ex)
            {
                queueFailed = true;
                operation.Release();
                if (ReferenceEquals(owner.SaveOperation, operation)) owner.SaveOperation = null;
                if (audioStop != null) audioStop.SaveQueued = false;
                if (go != null) Object.Destroy(go);
                Debug.LogWarning("[SpectreReplay] 无法排队保存（录音仍保留）: " + ex);
                if (notify) TriggerMessage(LocalizationManager.GetLocalizedText("note.save_fail"));
            }
        };
        if (audioStop != null)
        {
            audioStop.SaveQueued = true;
            audioStop.WhenCompleted(queue);
        }
        else queue(null);
        // 与已有录音封头延迟保存一样：true 表示请求已接受；结果由主线程事件/通知发布。
        return !queueFailed;
    }

    private sealed class SaveCompletion : MonoBehaviour
    {
        internal ReplaySaveOperation Operation;
        internal ReplayData Owner;
        internal bool Notify;
        internal bool AudioMissing;

        private void Update()
        {
            var operation = Operation;
            if (operation == null || !operation.IsCompleted) return;
            Operation = null; // 事件回调重入也只能完成一次。
            if (ReferenceEquals(Owner.SaveOperation, operation))
            {
                Owner.SaveOperation = null;
                // 接受请求不等于写盘成功；旧作业也不能清掉新一局的待保存状态。
                if (operation.Success && ReferenceEquals(data, Owner)) PendingSave = false;
            }
            operation.AllowRetry();
            try { CompleteSave(operation, Notify, AudioMissing); }
            finally
            {
                // 通知/云同步也必须在 Steam 退出前完成，不能在工作线程刚写完时放行退出。
                operation.Release();
                Object.Destroy(gameObject);
            }
        }
    }

    private static void CompleteSave(ReplaySaveOperation operation, bool notify, bool audioMissing)
    {
        if (operation.Error != null) Debug.LogWarning("[SpectreReplay] 保存收尾: " + operation.Error);
        string message = (operation.Success
            ? LocalizationManager.GetLocalizedText("note.save_success")
            : LocalizationManager.GetLocalizedText("note.save_fail")) + "\n";
        if (audioMissing || operation.AudioFailed)
            message += LocalizationManager.GetLocalizedText("note.keybdsoundfile_save_fail") + "\n";
        if (operation.BackupFailed)
            message += LocalizationManager.GetLocalizedText("note.save_fail") + "backup\n";
        if (operation.Success)
        {
            // 作业返回的是实际写出的完整路径，已包含格式后缀及安全名称重试结果。
            SpectreAPI.RaiseReplaySaved(operation.SavedPath);
            try
            {
                if (CloudReplay.OnReplaySaved(operation.SavedPath, operation.WavPath))
                    message += Ui.Loc.T("playback.cloud.saved") + "\n";
            }
            catch (Exception ex) { Debug.LogWarning("[SpectreReplay] 云同步入口异常（不影响本地保存）：" + ex.Message); }
        }
        if (notify) TriggerMessage(message);
    }

    internal enum NotifType { Info, Warning }

    /// <summary>
    /// 弹一条提示。
    ///
    /// <paramref name="extra"/> 是**在玩家设定值之上多加的秒数**（默认 0），
    /// 不是绝对时长——要读两行的错误提示写成 2，玩家把基础调大时它跟着变大。
    /// 基础值来自 <see cref="Settings.NotificationSeconds"/>，见
    /// <see cref="Settings.NotificationDuration"/>。
    /// </summary>
    internal static void TriggerMessage(string message, float extra = 0f, NotifType type = NotifType.Info)
    {
        if (debug_hide_text) return;
        // Notification 内部 StartCoroutine + UI 操作，后台线程直调会炸——转回主线程。
        // 带过去的是 extra 而不是解析后的秒数：在对侧解析一次就够了，
        // 传绝对秒数会让递归进来时又加一遍基础值。
        if (!MainThread.IsMainThread)
        {
            MainThread.Run(() => TriggerMessage(message, extra, type));
            return;
        }
        float dur = Settings.NotificationDuration(extra);
        // 2026-11-05 起改走自绘 Toast（深色圆角提示卡）：
        // 游戏 Notification 那条 UGUI 横幅样式老旧、没法做入场滑入/剩余时间条，
        // 且 RefreshLanguage 切换后它显示的是实例化时缓存的文案。自绘卡样式、
        // 时机、合并（同因 ×N）全部可控。时长口径不变：NotificationDuration(extra)。
        Ui.Toast.Show(message, dur, type == NotifType.Warning);
    }

    // 自动保存链的「按设置拒绝」统一出口：拒绝保存可以，但必须当场说明原因。
    // 静默丢局的表现就是「热键能存、出关不存」的无头案（2026-11-07 实测：
    // 全自动试玩 + DontSaveWhenAuto，从过关到退出整条链没有任何输出）。
    // 文案在调用点就地取词（键名要给死键扫描看见），reason 只进日志。
    internal static void NotifySaveSkipped(string message, string reason)
    {
        TriggerMessage(message, type: NotifType.Warning);
        Debug.Log("[SpectreReplay] replay not auto-saved: " + reason);
    }

    // angleOverride：回放推进兜底给出的"收进窗口"角度（ReplayPlayer.AdvanceClampAngle）；
    // 平时为 null = 用录制角度原样重判。
    public static bool ReplayHit(scrPlayer player, HitSnapshot hitdata)
        => ReplayHit(player, hitdata, null);

    public static bool ReplayHit(scrPlayer player, HitSnapshot hitdata, double? angleOverride)
        => ReplayPlayer.ReplayHit(player, hitdata, angleOverride);

    internal static void Play_keybd_event(bool flag)
        => ReplayPlayer.PlayKeyboardEvent(flag);

    internal static void Record_kbdstate(bool forceSync = false)
        => ReplayRecorder.RecordKeyboardState(forceSync);

    private static UnityEngine.Events.UnityAction<SkyHook.SkyHookEvent> _asyncKeyListener;

    public static void SetGetKeyAsyncEnabled(bool enabled)
    {
        if (enabled == UseAsyncInput)
        {
            if (!enabled)
            {
                AsyncKeyRecorder.PauseListener();
                if (is_recording) AsyncKeyRecorder.FlushFrame(force: true);
                else AsyncKeyRecorder.DiscardPending();
            }
            return;
        }

        if (enabled)
        {
            // Start the listener before sampling. Events arriving during the handoff
            // stay queued; the recorder reconciles late edges with the handoff snapshot
            // instead of discarding the first DOWN against the final sampled state.
            // 同局关闭时可能因缺少歌曲时钟留下了尾部事件；再次开启仍须保留。
            // 新局由 BeginRecording 清理，局外切换才可直接丢弃。
            if (!is_recording) AsyncKeyRecorder.DiscardPending();
            _asyncKeyListener = AsyncKeyRecorder.CreateListener();
            SkyHook.SkyHookManager.KeyUpdated.AddListener(_asyncKeyListener);
            bool asyncSyncStateRecorded = !is_recording;
            if (is_recording)
                asyncSyncStateRecorded = ReplayRecorder.RecordKeyboardState(forceSync: true, reconcileAsyncHandoff: true);
            else
                KeyboardSimulation.SampleKeyState(CurrKeyState);
            // On a missing clock, CurrKeyState is only an uncommitted observation. Seed
            // async de-duplication from PrevKeyState so queued edges remain recordable.
            AsyncKeyRecorder.SeedHeldState(asyncSyncStateRecorded ? CurrKeyState : PrevKeyState);
            UseAsyncInput = true;
            return;
        }

        // 与事件线程同锁关门，已在途的旧订阅回调即使稍后恢复也不能再次入队。
        // 排空后做一次同步记账，补上关门与采样之间发生的真实边沿，不能只改基线。
        AsyncKeyRecorder.PauseListener();
        if (_asyncKeyListener != null)
            SkyHook.SkyHookManager.KeyUpdated.RemoveListener(_asyncKeyListener);
        if (is_recording)
            AsyncKeyRecorder.FlushFrame(force: true);
        else
            AsyncKeyRecorder.DiscardPending();
        bool syncStateRecorded = !is_recording;
        if (is_recording)
            syncStateRecorded = ReplayRecorder.RecordKeyboardState(forceSync: true);
        else
            KeyboardSimulation.SampleKeyState(CurrKeyState);
        // If the conductor is temporarily unavailable, RecordKeyboardState deliberately
        // leaves PrevKeyState untouched. Keep that unrecorded edge visible to the next
        // synchronous poll instead of promoting the sample to a false baseline.
        if (syncStateRecorded)
            Array.Copy(CurrKeyState, PrevKeyState, CurrKeyState.Length);
        UseAsyncInput = false;
    }

    internal static void StopAll()
    {
        if (is_playing)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_playing"));
        }
        if (is_recording)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("note.stop_recording"));
        }
        PlayActions.StopPlaying();
        PlayActions.StopRecording();
    }

    internal static void CancelSave()
    {
        if (!PendingSave) return;
        PendingSave = false;
        LateSaveMode = false;
        TriggerMessage(LocalizationManager.GetLocalizedText("note.cancel_save"));
    }

    public static double ComputePercentXacc()
    {
        var tracker = scrController.instance?.playerOne?.marginTracker;
        if (tracker == null || tracker.hitMargins == null || tracker.hitMargins.Count == 0)
            return 0.0;
        return ComputePercentXaccVanilla();
    }

    // 原版口径 xacc：官方四段权重（1 / 0.75 / 0.4 / 0.2）对 margin 桶计数加权。
    // 文件名固定用这个口径——文件名要跨玩家通用可比。
    public static double ComputePercentXaccVanilla()
    {
        var tracker = scrController.instance?.playerOne?.marginTracker;
        if (tracker == null || tracker.hitMargins == null || tracker.hitMargins.Count == 0)
            return 0.0;
        // 分子与分母都交给兼容层：权重按名解析、逐版本复刻
        //   旧版：1.0×{Perfect,Auto} + 0.75×{Early,LatePerfect} + 0.4×{VeryEarly,VeryLate} + 0.2×{TooEarly,TooLate}
        //   r150：1.0×{XPerfect,PerfectMinus,PerfectPlus} + … + 0.2×{TooEarly,TooLate,FailedFloor}，Auto 不计入
        //
        // **分母不是 hitMargins.Count。** 后者含中旋 / 多压 / Auto 等无权重档位，
        // 拿它做分母会把全完美局算成 96%。官方式子是 playerHitMarginCount + FailedFloor。
        return HitMarginCompat.XAcc(HitMarginCounts);
    }

    public static double ComputePercentComplete(int curr_floor)
    {
        // lm 已加载但 listFloors 为空的窗口期（转场中/关卡构造中）会让除法得到
        // Infinity/NaN，直接透出到 UI 会显示成 "Infinity%"。分母为 0 时按 0 处理。
        if (scrController.instance != null && ADOBase.lm != null
            && ADOBase.lm.listFloors != null && ADOBase.lm.listFloors.Count > 0)
        {
            return (float)(curr_floor + 1) / (float)ADOBase.lm.listFloors.Count;
        }
        return 0.0;
    }

    internal static string CreateFileName(bool safe_mode = false)
    {
        if (safe_mode)
        {
            return DateTime.Now.ToString("yyyyMMddHHmmss") + "_";
        }
        string fileName = "";
        if (data.ArtistName != null && data.SongName != null)
        {
            fileName = data.ArtistName + "_" + data.SongName;
        }
        fileName = Regex.Replace(fileName, "<[^>]*>", string.Empty);
        fileName = DateTime.Now.ToString("yyyyMMddHHmmss") + "_" + fileName;
        try
        {
            bool isFullRun = data.EndTile == ADOBase.lm.listFloors.Count - 1 && data.StartTile == 0;
            if (isFullRun)
            {
                fileName += "_" + (100.0 * ComputePercentXaccVanilla()).ToString("F2");
            }
            else if (data.StartTile.HasValue && data.EndTile.HasValue)
            {
                // 缺起点/终点字段时旧版会抛 KeyNotFoundException 并被外层 catch 吞掉（不加后缀），
                // 这里显式跳过以保持一致，而不是把缺失当 0 处理。
                fileName += "_" + (100.0 * ComputePercentComplete(data.StartTile.Value)).ToString("F0")
                    + "-" + (100.0 * ComputePercentComplete(data.EndTile.Value)).ToString("F0");
            }
        }
        catch
        {
        }
        return ReplayManager.RemoveInvalidPathChars(fileName);
    }
}
