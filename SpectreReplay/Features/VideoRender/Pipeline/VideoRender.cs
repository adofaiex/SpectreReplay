using System;
using UnityEngine;
using SpectreReplay.API;
using SpectreReplay.Features.Replay;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.VideoRender;

// 回放视频渲染门面：UI 与补丁只跟这个类打交道，实际渲染流程由 VideoRenderJob 承载。
// 阶段与状态快照都集中在这里，方便设置页随时读取进度。
internal static class VideoRender
{
    internal enum Phase { Idle, Restarting, WaitingPlayback, Capturing, Finalizing }

    /// <summary>
    /// 上一次渲染的终态。
    ///
    /// **不能靠 <see cref="Snapshot.Output"/> 非空来推断「完成」**：Output 在渲染
    /// 一开始就写进去了（BuildOutputPath 之后立刻赋值），取消时它照样是非空的，
    /// 于是界面把「我取消了」显示成「渲染完成：xxx.mp4」。终态必须显式记录。
    /// </summary>
    internal enum Outcome { None, Completed, Failed, Canceled }

    /// <summary>
    /// 本次渲染的动态区间。未启用时保持整份回放语义。
    /// </summary>
    internal readonly struct RenderRange
    {
        internal readonly bool Enabled;
        internal readonly double StartSongPosition;
        internal readonly double EndSongPosition;

        internal RenderRange(bool enabled, double startSongPosition, double endSongPosition)
        {
            Enabled = enabled;
            StartSongPosition = startSongPosition;
            EndSongPosition = endSongPosition;
        }

        internal static RenderRange Full => new RenderRange(false, 0.0, 0.0);

        internal static RenderRange FromTimeline()
        {
            var state = TimelineController.State;
            if (state.Source == null || state.Duration <= 0.0)
                return Full;
            var selection = state.Selection;
            return new RenderRange(true, selection.Start, selection.End);
        }
    }

    internal static RenderRange PendingRange = RenderRange.Full;

    // 渲染状态快照（全部主线程读写，无锁）
    internal sealed class Snapshot
    {
        internal string Stage = "";
        internal string Detail = "";
        internal string Output = "";
        internal string Error = "";
        internal Outcome Result = Outcome.None;
        internal int FramesDone;
        internal int FramesTotal;
        internal float Progress => FramesTotal > 0
            ? Mathf.Clamp01((float)FramesDone / FramesTotal)
            : 0f;
        // 捕获阶段预计剩余秒数；<0 = 暂不可估（捕获未开始/已进入收尾）
        internal double EtaSeconds = -1;
    }

    internal static Phase CurrentPhase { get; set; } = Phase.Idle;
    internal static Snapshot Current { get; } = new Snapshot();
    internal static VideoRenderJob Job { get; private set; }
    internal static bool Active => Job != null;

    // 当前帧源的捕获面。帧源构造时装上、构造失败（CleanupOnConstructionFailure）
    // 与 Dispose 时清掉，StartRender 的 ResetLeakedPins 兜底复位。渲染期补丁按它
    // 分流：判定飘字隐藏只拦 CleanCamera——WholeScreen 拍的就是后备缓冲，飘字
    // 本来就在屏上。放在门面而非帧源实例：补丁拿不到 frames 引用，门面纪律见类注释。
    internal static VideoSurface? CurrentSurface { get; set; }

    // —— 时间钉扎 ——
    // 作业锚定时间线后置位，VideoRenderPatches 把 conductor 的读写都钉在这一个值上，
    // 游戏各系统（轨道推进、判定、回放调度）就按输出帧率逐帧推进，与真实音频时钟脱钩。
    internal static double PinnedSongPosition;

    // ── 星球读值修正（最终结论：全部回退，星球读钉扎原值）──────
    // 五版实测的最终结论：渲染期**不要**给星球另设时间源。
    //   · 提前一帧（v4/v5）：同曲同段 A/B 对比——旧成片（无补丁）星球贴砖，
    //     新成片星球恒定前冲一个可视步长（60fps/BPM 高的图一帧 16°+），再叠
    //     判定点钳制的「停一帧跳两帧」，观感上就是「星球飘移」；
    //   · 钉扎层相位补偿（v1/v2）：反馈回路，整体推迟一帧；
    //   · 事件残差补偿（v3）：方向反了。
    // 判定走 hitTick（精确事件时刻），与星球视觉本就无相位冲突；快歌单帧
    // 大步进是帧率量化（物理上限），时钟层无解。星球角度与砖块/判定共用
    // 同一个钉扎值——这正是旧成片「贴砖」的形态。

    // ── 关于「星球越过真实判定点」的结论（实测后回退）──────
    // 试过两版补偿，都被数据否掉，最终**不修改钉扎时钟**：
    //   ① 逐事件对齐：Δt 忽大忽小 → 星球掉帧感；
    //   ② 按事件残差扣整砖相位：日志显示 1977 次命中的残差全部顶在夹取上限
    //      （20.0ms = 一帧 @60fps×1.2）。根因是**反馈回路**——按键事件消费读的
    //      就是 PinnedSongPosition，扣残差等于把整条回放整体推迟一帧，下一帧
    //      测到的残差更大，永远顶格。净效果是「判定/星球一起慢一帧」，比不修更糟。
    // 结论：判定走 hitTick（精确事件时刻，本就不受影响），星球读帧末时刻，
    // 二者差 ≤1 帧是帧采样率的固有量化，不做时钟层补偿；命中触发帧与越界帧
    // 的对齐由 Patch_PlayerControlUpdate.GetAngleThisFrame 负责（本帧现算）。
    internal static bool TimePinned;

    internal static bool TryReadPinnedPosition(out double position)
    {
        position = PinnedSongPosition;
        return TimePinned && Active;
    }

    internal static void PinTimeline(double songPosition)
    {
        PinnedSongPosition = songPosition;
        TimePinned = true;
    }

    internal static void UnpinTimeline()
    {
        TimePinned = false;
        PinnedSongPosition = 0.0;
    }

    // —— 渲染期倍速钉扎 ——
    // scnEditor.Play() 对程序化调用**无条件**把倍速重置成 1x（源码
    // scnEditor.cs:7204-7205：`holdingControl` 只有真按住 Ctrl 才为真），
    // 随后 RemakePath → SetupConductorWithLevelData 用这个值算 song.pitch。
    // 而 TryStartPlayback 的倍速校验拿它与回放记录比对——渲染自己重建关卡，
    // 条件就必须由渲染自己准备，不能要求玩家先按住 Ctrl 再点渲染。
    internal static bool SpeedOverrideArmed;
    internal static float SpeedOverride = 1f;
    internal static bool SpeedOverrideQuickPitched;

    // RemakePath 的 Prefix 与 Start_Rewind 的 Prefix 共用这一处写入。
    // 必须在 RemakePath 之前写回：pitch 是在 RemakePath 里推导的，
    // 晚于它的写入只会影响后续判定、救不回已经算错的时间轴。
    internal static void ApplySpeedOverrideTo(scnEditor editor)
    {
        if (!SpeedOverrideArmed || editor == null)
            return;
        editor.playbackSpeed = SpeedOverride;
        GCS.editorQuickPitchedPlaying = SpeedOverrideQuickPitched;
    }

    // 设置页「开始渲染」按钮入口
    internal static void StartRender()
    {
        StartRender(RenderRange.FromTimeline());
    }

    internal static void StartRender(RenderRange range)
    {
        if (Job != null)
            return;
        if (!Settings.RenderOn)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.disabled"), type: NotifType.Warning);
            return;
        }
        if (SpectreState.data == null || SpectreState.data.HitSnapshots.Count == 0)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.need_replay"), type: NotifType.Warning);
            return;
        }
        bool inLevel = scrController.instance != null && ADOBase.lm != null && ADOBase.lm.listFloors != null
            && ADOBase.lm.listFloors.Count > 1;
        bool inEditor = ADOBase.editor != null && ADOBase.editor.floors != null && ADOBase.editor.floors.Count > 1;
        if (!inLevel && !inEditor)
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.need_level"), type: NotifType.Warning);
            return;
        }
        string encoderPath = VideoEncoder.LocateEncoder();
        // **启动前能力校验（2026-11-02 审计）**：Locate 已对本地候选跑过
        // -version（缓存命中零成本）；这里对最终选择（含 PATH 回落）统一再
        // 门槛一次——坏 ffmpeg 在劫持相机之前就被拦下，报「缺少 ffmpeg」
        // 而不是渲染中途离奇失败。
        System.Diagnostics.Stopwatch clickSw = System.Diagnostics.Stopwatch.StartNew();
        if (!VideoEncoder.VerifyEncoder(encoderPath))
        {
            TriggerMessage(LocalizationManager.GetLocalizedText("rendervideo.ffmpeg_missing"), extra: 2f, type: NotifType.Warning);
            return;
        }
        Debug.Log("[SpectreReplay] render timing [click-verified] "
            + clickSw.ElapsedMilliseconds + "ms");
        ResetLeakedPins();
        ResetSnapshot();
        CurrentPhase = Phase.Restarting;
        PendingRange = range;
        Job = VideoRenderJob.Launch(encoderPath, range);
        SpectreAPI.RaiseRenderStarted();
    }

    internal static void CancelRender()
    {
        Job?.RequestCancel();
    }

    /// <summary>
    /// 玩家按 Esc 退出试玩 / 游戏把我们踢出 playtest 时调用（见 Patch_SwitchToEditMode）。
    ///
    /// **必须立刻、同步地把游戏交还回去**，而不是只给作业打个取消标记：
    /// Esc 触发的 <c>SwitchToEditMode</c> 会紧接着恢复相机、停掉 conductor，
    /// 而渲染作业每帧还在推进钉扎时钟、预览相机每帧还在往 backbuffer 贴旧帧。
    /// 两者叠加就是「镜厅特效 + 卡死」。
    /// </summary>
    internal static void CancelForSceneExit()
    {
        if (SuppressSceneExitCancel) return;
        Job?.CancelForSceneExit();
    }

    /// <summary>
    /// 我们自己主动调用 <c>SwitchToEditMode</c> 时置位，避免把正常收尾误判成
    /// 「玩家中途退出」而把一次成功的渲染改判成取消。
    /// 只在主线程读写。
    /// </summary>
    internal static bool SuppressSceneExitCancel;

    /// <summary>捕获循环是否正在跑。Start_Rewind 在捕获期间再次触发 = 时间轴被重置。</summary>
    internal static bool CaptureInProgress;

    /// <summary>捕获期间回放被重新开始（Start_Rewind 二次触发）：通知作业中止。</summary>
    internal static void NotifyPlaybackRestarted()
    {
        if (CaptureInProgress)
            Job?.AbortOnPlaybackRestart();
    }

    internal static void ResetLeakedPins()
    {
        UnpinTimeline();
        RenderClockSource.ActiveClock = null;
        SpeedOverrideArmed = false;
        CurrentSurface = null;   // 上一轮帧源若异常退出没清干净，别让飘字门误判
    }

    // 作业结束时的收尾（无论成败），由 VideoRenderJob 调用
    internal static void CloseJob()
    {
        UnpinTimeline();
        // 不复位的话，下一轮启动阶段的 Start_Rewind 会被 NotifyPlaybackRestarted
        // 误判成「捕获期重启」，作业刚起步就被自己掐掉。
        CaptureInProgress = false;
        Job = null;
        CurrentPhase = Phase.Idle;
    }

    private static void ResetSnapshot()
    {
        Current.Stage = "";
        Current.Detail = "";
        Current.Output = "";
        Current.Error = "";
        Current.Result = Outcome.None;
        Current.FramesDone = 0;
        Current.FramesTotal = 0;
        Current.EtaSeconds = -1;
    }
}
