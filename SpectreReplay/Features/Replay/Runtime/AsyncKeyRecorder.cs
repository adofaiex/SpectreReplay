using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SkyHook;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.Replay;

// 异步输入录制：在 SkyHook 事件线程上捕获原始按键，主线程每帧统一换算成歌曲时间。
// 换算必须推迟到主线程：dspTimeSong / addoffset 等导体状态只在主线程更新，
// 事件线程直接读会拿到撕裂的基准。KeyEvent 结构保持不变，旧回放文件照常播放。
internal static class AsyncKeyRecorder
{
    private static readonly ConcurrentQueue<SkyHookEvent> Pending = new();
    private static readonly object EventGate = new();
    private static bool _accepting;
    private static int _listenerGeneration;
    // 来源持键由同步快照建立；交接期间的原始边沿可回溯修正快照，不能直接丢弃。
    private static readonly bool[] HeldAtLastEdge = new bool[256];
    private static bool _hasClock, _clockWarningShown;
    private static double _clockPitch, _clockSongTime, _clockCalibration, _clockOffset;
    private static long _inputGapTicks;
    private sealed class SynchronousHandoff
    {
        internal ReplayData Owner;
        internal byte[] Before, Sample;
        internal long SampleStarted, SampleTicks;
        internal int PrefixCount;
        internal double Position, RawPosition, Pitch, LowerBound;
        internal readonly List<HandoffEdge> Raw = new();
        internal readonly HashSet<KeyEvent> Published = new();
        internal readonly bool[] Observed = new bool[256];
        internal readonly List<HandoffObservation> Tail = new();
        internal readonly HashSet<KeyEvent> KnownTail = new();
        internal int TailCapturedThrough;
    }
    private struct HandoffEdge
    {
        internal ushort Key;
        internal bool Pressed;
        internal long Ticks;
        internal int Order;
    }
    private struct HandoffObservation
    {
        internal KeyEvent Edge;
        // Edge=null 表示全键释放边界，只存一项；迟交重建时按当时持键补UP。
        internal double ReleasePosition;
    }
    private static SynchronousHandoff synchronousHandoff;

    internal static void BeginRecording()
    {
        lock (EventGate)
        {
            DiscardPending();
            _accepting = true;
        }
        Array.Clear(HeldAtLastEdge, 0, HeldAtLastEdge.Length);
        _hasClock = false;
        _clockWarningShown = false;
        _inputGapTicks = 0;
        synchronousHandoff = null;
    }

    internal static void EndRecording()
    {
        // 最终排空已关闭事件接纳，不再需要交接历史；不要静态保留旧会话及录音。
        synchronousHandoff = null;
    }

    // 帧内复用缓冲 + 缓存的比较器：事件本身按到达序排队（SkyHook 单线程回调），
    // 排序只是防时钟乱序的保险，且避免每帧分配
    private static readonly List<SkyHookEvent> FrameBuffer = new();
    private static readonly Comparison<SkyHookEvent> ByEventTick = (a, b)
        => a.GetTimeInTicks().CompareTo(b.GetTimeInTicks());

    // 每次订阅使用独立代际，RemoveListener 前已在途的旧回调不能跨开关复活。
    internal static UnityEngine.Events.UnityAction<SkyHookEvent> CreateListener()
    {
        lock (EventGate)
        {
            int generation = ++_listenerGeneration;
            _accepting = true;
            return keyEvent =>
            {
                lock (EventGate)
                {
                    if (generation != _listenerGeneration) return;
                    OnKeyUpdated(keyEvent);
                }
            };
        }
    }

    internal static void PauseListener()
    {
        lock (EventGate)
        {
            _accepting = false;
            _listenerGeneration++;
        }
    }

    // SkyHookManager.KeyUpdated 回调（事件线程）
    internal static void OnKeyUpdated(SkyHookEvent keyEvent)
    {
        // 鼠标不参与键盘回放
        if (keyEvent.Label == KeyLabel.MouseLeft || keyEvent.Label == KeyLabel.MouseMiddle
            || keyEvent.Label == KeyLabel.MouseRight || keyEvent.Label == KeyLabel.MouseX1
            || keyEvent.Label == KeyLabel.MouseX2)
            return;
        // 与最终停止共用接纳锁：关门之前接纳的边沿全部排空，之后属于局外。
        lock (EventGate)
        {
            if (!_accepting) return;
            Pending.Enqueue(keyEvent);
        }
    }

    // 不录制期间持续丢弃，避免积压的旧事件在下一次录制开始时混入
    internal static void DiscardPending()
    {
        while (Pending.TryDequeue(out _)) { }
    }

    // Set the event-source baseline from the synchronous keyboard snapshot.
    // Pending is intentionally preserved: events captured during subscription setup
    // are filtered against this state instead of being lost in the handoff window.
    internal static void SeedHeldState(byte[] currentState)
    {
        Array.Clear(HeldAtLastEdge, 0, HeldAtLastEdge.Length);
        if (currentState == null) return;
        int length = Math.Min(currentState.Length, HeldAtLastEdge.Length);
        for (int i = 0; i < length; i++)
            HeldAtLastEdge[i] = (currentState[i] & 0x80) != 0;
    }

    // 采样已经完成，但其间的内核事件可能还没交付。保留交接前基线与完整快照，
    // 原始边沿到达后只重建这个固定小段，不让最终采样状态吞掉更早的短点击。
    // 采样期间发生的边沿可能比逐键快照新；后续第一次观察则固定其后的状态，
    // 包括当时与旧基线相同、尚未发布成 KeyEvent 的观察。
    internal static void RecordSynchronousHandoff(scrConductor conductor, long sampleStarted, long sampleFinished)
    {
        long nowTicks = DateTime.Now.Ticks;
        double dsp = AudioSettings.dspTime;
        double lowerBound = data.KeyEvents.Count == 0 ? double.NegativeInfinity
            : data.KeyEvents[data.KeyEvents.Count - 1].SongPosition;
        synchronousHandoff = new SynchronousHandoff
        {
            Owner = data,
            Before = (byte[])PrevKeyState.Clone(),
            Sample = (byte[])CurrKeyState.Clone(),
            SampleStarted = sampleStarted,
            SampleTicks = sampleFinished,
            PrefixCount = data.KeyEvents.Count,
            Position = Math.Max(lowerBound, conductor.songposition_minusi),
            RawPosition = conductor.song.pitch * (dsp + (sampleFinished - nowTicks) / 10000000.0 - conductor.dspTimeSong
                - scrConductor.calibration_i) - conductor.addoffset,
            Pitch = conductor.song.pitch,
            LowerBound = lowerBound
        };
        RebuildSynchronousHandoff(synchronousHandoff);
        Array.Copy(CurrKeyState, PrevKeyState, CurrKeyState.Length);
    }

    private static void RebuildSynchronousHandoff(SynchronousHandoff handoff)
    {
        var events = handoff.Owner.KeyEvents;
        CaptureHandoffTail(handoff);
        events.RemoveRange(handoff.PrefixCount, events.Count - handoff.PrefixCount);
        handoff.Published.Clear();
        byte[] held = (byte[])handoff.Before.Clone();
        var duringSample = new HashSet<ushort>();
        handoff.Raw.Sort((a, b) => a.Ticks != b.Ticks
            ? a.Ticks.CompareTo(b.Ticks) : a.Order.CompareTo(b.Order));
        foreach (HandoffEdge edge in handoff.Raw)
        {
            if (edge.Ticks >= handoff.SampleStarted) duringSample.Add(edge.Key);
            if (((held[edge.Key] & 0x80) != 0) == edge.Pressed) continue;
            held[edge.Key] = (byte)(edge.Pressed ? 128 : 0);
            if (ManagedInput.IsModHotkey(edge.Key)) continue;
            // 同步时钟可能只在上一帧更新过；只在两次已知采样之间解释这段边沿，
            // 不允许晚到的旧 tick 跑到前一次已提交状态之前或本次快照之后。
            double position = handoff.RawPosition
                + (edge.Ticks - handoff.SampleTicks) / 10000000.0 * handoff.Pitch;
            PublishHandoffEdge(handoff, edge.Key, edge.Pressed,
                Math.Max(handoff.LowerBound, Math.Min(handoff.Position, position)));
        }
        for (ushort key = 1; key < held.Length; key++)
        {
            bool pressed = (handoff.Sample[key] & 0x80) != 0;
            // 采样期间的事件可能发生在这个键已被读过之后；该键以事件末态为准，
            // 不用一个可能更早的快照捏造相反边沿。完全无回调的键仍由快照补记。
            if (duringSample.Contains(key)) continue;
            if (((held[key] & 0x80) != 0) == pressed) continue;
            held[key] = (byte)(pressed ? 128 : 0);
            if (ManagedInput.IsModHotkey(key)) continue;
            PublishHandoffEdge(handoff, key, pressed, handoff.Position);
        }
        // 重放完整观察后缀，而非只看已发布事件：先到的UP可能被旧up基线去重，
        // 迟到DOWN修正交接后，该UP必须重新成为真实边沿。已发布但现在重复的
        // 后缀也保留在Tail里，供下一次迟交重建重新判断，避免重复或遗失。
        foreach (HandoffObservation observation in handoff.Tail)
        {
            KeyEvent edge = observation.Edge;
            if (edge == null)
            {
                for (ushort key = 0; key < held.Length; key++)
                {
                    if ((held[key] & 0x80) == 0) continue;
                    held[key] = 0;
                    if (ManagedInput.IsModHotkey(key)) continue;
                    var release = new KeyEvent { KeyCode = key, IsPressed = false, SongPosition = observation.ReleasePosition };
                    events.Add(release);
                    handoff.KnownTail.Add(release);
                }
                continue;
            }
            if (((held[edge.KeyCode] & 0x80) != 0) == edge.IsPressed) continue;
            held[edge.KeyCode] = (byte)(edge.IsPressed ? 128 : 0);
            if (!ManagedInput.IsModHotkey(edge.KeyCode)) events.Add(edge);
        }
        handoff.TailCapturedThrough = events.Count;
        foreach (ushort key in duringSample)
        {
            HeldAtLastEdge[key] = (held[key] & 0x80) != 0;
            PrevKeyState[key] = held[key];
        }
    }

    private static void CaptureHandoffTail(SynchronousHandoff handoff)
    {
        var events = handoff.Owner.KeyEvents;
        for (int i = Math.Max(handoff.TailCapturedThrough, handoff.PrefixCount + handoff.Published.Count); i < events.Count; i++)
            if (handoff.KnownTail.Add(events[i])) handoff.Tail.Add(new HandoffObservation { Edge = events[i] });
        handoff.TailCapturedThrough = events.Count;
    }

    private static KeyEvent ObserveHandoffState(ushort key, bool pressed, double position)
    {
        var handoff = synchronousHandoff;
        if (handoff == null || !ReferenceEquals(handoff.Owner, data)) return null;
        // 第一次绝对状态观察之后，普通去重便不再依赖交接基线。仅额外保留
        // 每键的第一次观察；后续真实边沿复用已发布对象，不复制整局原始回调。
        // 热键不发布到回放，故其后续状态也须显式保留，防回溯覆盖实时基线。
        if (handoff.Observed[key] && !ManagedInput.IsModHotkey(key)) return null;
        CaptureHandoffTail(handoff);
        handoff.Observed[key] = true;
        var edge = new KeyEvent { KeyCode = key, IsPressed = pressed, SongPosition = position };
        handoff.Tail.Add(new HandoffObservation { Edge = edge });
        handoff.KnownTail.Add(edge);
        return edge;
    }

    internal static void ObserveSynchronousState(double position)
    {
        if (synchronousHandoff == null || !ReferenceEquals(synchronousHandoff.Owner, data)) return;
        for (ushort key = 1; key < CurrKeyState.Length; key++)
            ObserveHandoffState(key, (CurrKeyState[key] & 0x80) != 0, position);
    }

    private static SynchronousHandoff ObserveGapRelease(double position)
    {
        var handoff = synchronousHandoff;
        if (handoff == null || !ReferenceEquals(handoff.Owner, data)) return null;
        CaptureHandoffTail(handoff);
        // 断档是新的强制基线，不能只保留每键第一次观察：当时up的键也可能
        // 有尚未交付的旧DOWN，之后重建必须仍在这个边界释放它。
        handoff.Tail.Add(new HandoffObservation { ReleasePosition = position });
        return handoff;
    }

    private static void PublishHandoffEdge(SynchronousHandoff handoff, ushort key, bool pressed, double position)
    {
        var edge = new KeyEvent { KeyCode = key, IsPressed = pressed, SongPosition = position };
        var events = handoff.Owner.KeyEvents;
        // 固定前缀之后整体替换小段；后续同步采样可能拥有完全相同的歌曲时间，
        // 仍必须排在交接之后。全局按时间 upper_bound 会把后续 UP 反插到旧 DOWN 前。
        events.Insert(handoff.PrefixCount + handoff.Published.Count, edge);
        handoff.Published.Add(edge);
    }

    // UpdateInput 的 Prefix 每帧调用（主线程）。force 排空关闭监听后的旧模式队列；
    // final 只用于最终停止：不会再等到下一帧，缺时钟也必须显式降级保全边沿。
    internal static void FlushFrame(bool force = false, bool final = false)
    {
        if (final)
        {
            lock (EventGate) _accepting = false;
        }
        if (!is_recording || data == null || (!force && !Settings.GetDataFromAsyncInput))
        {
            DiscardPending();
            return;
        }
        if (!force && (!Application.isFocused || !AsyncInputManager.isActive))
        {
            if (_inputGapTicks == 0) _inputGapTicks = DateTime.Now.Ticks;
            DiscardPending();
            return;
        }
        // **取基准之前必须确认导体可用。** 换算式的每一项都挂在导体上，而
        // scrController.UpdateInput 在结算转场/场景卸载期间仍会跑，此时
        // ADOBase.conductor 可能已经销毁、或 song 尚未就绪。
        //
        // 这个判断的位置很关键：必须放在**出队之前**。原实现先 while 把 Pending
        // 全部搬进 FrameBuffer、再在循环体里解引用，异常被外层补丁的 catch 吞掉
        // ——事件已经从队列里拿走了，于是那一批按键彻底丢失，而玩家只看到
        // "我明明按了却没录上"。这里提前返回则事件留在队列里，下一帧导体就绪后
        // 照常换算（SkyHook 时间戳是绝对 tick，晚一帧换算不影响精度）。
        bool approximateClock = false;
        scrConductor conductor = ADOBase.conductor;
        if (conductor != null && conductor.song != null)
        {
            _clockPitch = conductor.song.pitch;
            _clockSongTime = conductor.dspTimeSong;
            _clockCalibration = scrConductor.calibration_i;
            _clockOffset = conductor.addoffset;
            _hasClock = true;
            _clockWarningShown = false;
        }
        else if (!force || !_hasClock)
        {
            if (!final)
            {
                // 普通模式切换还会有下一帧，保留队列等待精确换算。
                if (force && Pending.TryPeek(out _) && !_clockWarningShown)
                {
                    _clockWarningShown = true;
                    Debug.LogWarning("[SpectreReplay] 异步尾部事件缺少歌曲时钟，保留到导体恢复后处理");
                }
                return;
            }
            // Stop 随后就会保存/替换 data，不能假称还能等导体恢复。没有任何
            // 歌曲基准便不可能恢复绝对时间；以最后记录位置（空录制为0）为锚，
            // 按1x保留尾部事件的相对间隔，并明确记录这是近似时间。
            approximateClock = true;
            if (Pending.TryPeek(out _))
                Debug.LogWarning("[SpectreReplay] 停止录制从未获得歌曲时钟：尾部按键按最后记录位置（无记录为0）及1x相对间隔保全，时间为近似值");
        }
        if (_inputGapTicks == 0 && !Pending.TryPeek(out _)) return;

        // SkyHook 时间戳是绝对 tick，与基准何时取样无关；一帧只取一对基准，
        // dspTime 与系统时钟取得越近换算越准
        long nowTicks = DateTime.Now.Ticks;
        long dspNowTicks = (long)(AudioSettings.dspTime * 10000000.0);

        if (_inputGapTicks != 0)
        {
            // 断档内仍丢弃局外输入；恢复时补齐旧持键的松开，再接收新按下。
            // 无导体时保留断档，沿用下方正常边沿的时钟等待/最终降级规则。
            double lastPosition = data.KeyEvents.Count == 0 ? 0
                : data.KeyEvents[data.KeyEvents.Count - 1].SongPosition;
            double gapDsp = (_inputGapTicks + dspNowTicks - nowTicks) / 10000000.0;
            double gapPosition = approximateClock ? lastPosition : Math.Max(lastPosition,
                _clockPitch * (gapDsp - _clockSongTime - _clockCalibration) - _clockOffset);
            SynchronousHandoff gapHandoff = ObserveGapRelease(gapPosition);
            for (ushort key = 0; key < HeldAtLastEdge.Length; key++)
            {
                if (!HeldAtLastEdge[key]) continue;
                HeldAtLastEdge[key] = false;
                PrevKeyState[key] = 0;
                if (!ManagedInput.IsModHotkey(key))
                {
                    var release = new KeyEvent { KeyCode = key, IsPressed = false, SongPosition = gapPosition };
                    data.KeyEvents.Add(release);
                    // 已由上面的全键边界表示，不重复捕获为普通后缀观察。
                    gapHandoff?.KnownTail.Add(release);
                }
            }
            _inputGapTicks = 0;
        }
        if (!Pending.TryPeek(out _)) return;
        FrameBuffer.Clear();
        while (Pending.TryDequeue(out SkyHookEvent ev))
            FrameBuffer.Add(ev);
        FrameBuffer.Sort(ByEventTick);
        long firstTick = (long)FrameBuffer[0].GetTimeInTicks();
        double fallbackPosition = data.KeyEvents.Count == 0 ? 0
            : data.KeyEvents[data.KeyEvents.Count - 1].SongPosition;

        bool rebuildHandoff = false;
        foreach (SkyHookEvent ev in FrameBuffer)
        {
            if (ev.Key >= HeldAtLastEdge.Length) continue;
            long eventTicks = (long)ev.GetTimeInTicks();
            if (synchronousHandoff != null && ReferenceEquals(synchronousHandoff.Owner, data)
                && eventTicks <= synchronousHandoff.SampleTicks)
            {
                synchronousHandoff.Raw.Add(new HandoffEdge { Key = ev.Key, Pressed = ev.Type == SkyHook.EventType.KeyPressed,
                    Ticks = eventTicks, Order = synchronousHandoff.Raw.Count });
                rebuildHandoff = true;
            }
        }
        if (rebuildHandoff) RebuildSynchronousHandoff(synchronousHandoff);
        foreach (SkyHookEvent ev in FrameBuffer)
        {
            if (ev.Key >= HeldAtLastEdge.Length) continue;
            if (synchronousHandoff != null && ReferenceEquals(synchronousHandoff.Owner, data)
                && (long)ev.GetTimeInTicks() <= synchronousHandoff.SampleTicks) continue;
            bool pressed = ev.Type == SkyHook.EventType.KeyPressed;
            double dsp = ((long)ev.GetTimeInTicks() + dspNowTicks - nowTicks) / 10000000.0;
            double songPos = approximateClock
                ? fallbackPosition + ((long)ev.GetTimeInTicks() - firstTick) / 10000000.0
                : _clockPitch * (dsp - _clockSongTime - _clockCalibration) - _clockOffset;
            KeyEvent observed = ObserveHandoffState(ev.Key, pressed, songPos);
            if (HeldAtLastEdge[ev.Key] == pressed) continue;
            HeldAtLastEdge[ev.Key] = pressed;
            PrevKeyState[ev.Key] = (byte)(pressed ? 128 : 0);
            // 本模组热键不录（原因见 ManagedInput.IsModHotkey）：它们会在回放时
            // 被当成玩法输入，导致非编辑器关卡在结算画面自动退出。状态仍更新，
            // 这样热键抬起不会污染之后的基线。
            if (ManagedInput.IsModHotkey(ev.Key))
                continue;
            data.KeyEvents.Add(observed ?? new KeyEvent
            {
                KeyCode = ev.Key,
                IsPressed = pressed,
                SongPosition = songPos
            });
        }
    }
}
