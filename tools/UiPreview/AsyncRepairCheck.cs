using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

internal static class AsyncRepairCheck
{
    internal static int Run()
    {
        int fail = 0;
        void Check(bool ok, string label) { Console.WriteLine("  " + (ok ? "✓ " : "✗ ") + label); if (!ok) fail++; }
        var recorder = CSharpSyntaxTree.ParseText(SrcUtil.Source(Driver.ProjectDir, "Features/Replay/Runtime/ReplayRecorder.cs")).GetRoot();
        string Method(string name) => recorder.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name).ToString();
        var stopMethod = recorder.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Stop");
        string stopBody = string.Join("\n", stopMethod.Body.Statements);
        var state = CSharpSyntaxTree.ParseText(SrcUtil.Source(Driver.ProjectDir, "State/SpectreState.cs")).GetRoot();
        string toggle = state.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "SetGetKeyAsyncEnabled").ToString();
        string fixture = Fixture.Replace("__STOP__", stopBody, StringComparison.Ordinal)
            .Replace("__TOGGLE__", toggle, StringComparison.Ordinal)
            .Replace("__POLL__", Method("RecordKeyboardState"), StringComparison.Ordinal);
        try
        {
            string real = SrcUtil.Source(Driver.ProjectDir, "Features/Replay/Runtime/AsyncKeyRecorder.cs");
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("AsyncRepair_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(real), CSharpSyntaxTree.ParseText(fixture) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(6)));
            var result = (bool[])Assembly.Load(image.ToArray()).GetType("Fixture").GetMethod("Run").Invoke(null, null);
            Check(result[0], "AS01 导体销毁前缓存时钟，停止仍记录尾部按下/抬起并同步轮询基线");
            Check(result[1], "AS02 暂无时钟保留队列，导体恢复后精确消费一次");
            Check(result[2], "AS03 新录制清空来源去重状态，不吞同来源的首次按下");
            Check(result[3], "AS06 真实Stop保存无时钟尾部边沿及相对间隔并告警，重复停止不重存");
            Check(result[4], "AS07 最终停止先关闭事件接纳，排空期间的并发回调不滞留且下一局重新开放");
            Check(result[5], "AS08 实际监听切换补记同步边沿，在途旧回调跨采样和重启监听不重复入队");
            Check(result[6], "AS09 MissingClockHandoff: 缺导体交接不提交未记账基线，旧回调失效后恢复时钟补记持键一次");
            Check(result[7], "AS10 SyncToAsyncMissingClock: 新按下沿在时钟恢复后补记，已记账持键的抬起不丢");
            for (int gap = 0; gap < 4; gap++)
                Check(result[8 + gap], "AS11 失焦/输入停用断档协调已录按键，恢复首键不丢且无局外按键 " + gap);
            Check(result[12], "AS12 新录制清除旧输入断档，不释放新局的同步基线");
            Check(result[13], "AS13 同局缺时钟关闭再开启保留完整点击，重启前后恢复时钟都只消费一次");
            for (int handoff = 0; handoff < 6; handoff++)
                Check(result[14 + handoff], "AS14 同步转异步保全采样期间及迟交的三连边沿、原始间隔和后续状态 " + handoff);
            Check(result[20], "AS15 迟交重建保留同歌曲时间的后续同步UP顺序，保存末态与实时状态一致");
            for (int delivery = 0; delivery < 2; delivery++)
                Check(result[21 + delivery], "AS16 采样已经读完的键以窗口内真实边沿为准，不补虚假UP且后续UP不被去重 " + delivery);
            for (int direction = 0; direction < 2; direction++)
            {
                Check(result[23 + direction], "AS17 较新原始边沿先被去重再迟交采样内边沿，仍保全完整点击与末态 " + direction);
                Check(result[25 + direction], "AS18 后续同步采样即使未发布边沿，也能纠正迟交采样内边沿的末态 " + direction);
                Check(result[27 + direction], "AS19 失焦恢复强制松键基线能覆盖迟交采样内边沿，不复活失焦前持键 " + direction);
            }
            Check(result[29], "AS20 停止录制释放交接历史，不继续持有被替换的旧回放和录音");
        }
        catch (Exception ex) { Check(false, "AS01-10 真实异步录制器执行: " + (ex.InnerException ?? ex).Message); }
        string stop = Method("Stop"), start = Method("Start"), poll = Method("RecordKeyboardState");
        Check(stop.IndexOf("AsyncKeyRecorder.FlushFrame(force: true, final: true);", StringComparison.Ordinal) is int at && at >= 0
            && at < stop.IndexOf("is_recording = false;", StringComparison.Ordinal), "AS04 停止先排空异步尾部，再翻录制标志");
        Check(start.Contains("AsyncKeyRecorder.BeginRecording();", StringComparison.Ordinal)
            && poll.Contains("if (!UseAsyncInput) AsyncKeyRecorder.FlushFrame(force: true);", StringComparison.Ordinal),
            "AS05 每局重置队列，关闭监听后的保留事件仍有同步消费入口");
        return fail;
    }

    private const string Fixture = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SpectreReplay;
using SpectreReplay.Features.Replay;
using static SpectreReplay.SpectreState;
using SpectreReplay.Features.Compat;
using UnityEngine;
public static class Fixture
{
    static void Stop() { __STOP__ }
    public static bool[] Run()
    {
        SpectreState.is_recording = true;
        SpectreState.data = new ReplayData();
        AsyncKeyRecorder.BeginRecording();
        ADOBase.conductor = new scrConductor();
        AsyncKeyRecorder.FlushFrame();
        ADOBase.conductor = null;
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, true));
        AsyncKeyRecorder.FlushFrame(force: true);
        bool down = SpectreState.PrevKeyState[65] == 128;
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, false));
        AsyncKeyRecorder.FlushFrame(force: true);
        bool cached = down && SpectreState.data.KeyEvents.Count == 2 && SpectreState.PrevKeyState[65] == 0;
        AsyncKeyRecorder.BeginRecording();
        SpectreState.data = new ReplayData();
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, true));
        AsyncKeyRecorder.FlushFrame(force: true);
        bool preserved = SpectreState.data.KeyEvents.Count == 0;
        ADOBase.conductor = new scrConductor();
        AsyncKeyRecorder.FlushFrame(force: true);
        AsyncKeyRecorder.FlushFrame(force: true);
        preserved &= SpectreState.data.KeyEvents.Count == 1;
        AsyncKeyRecorder.BeginRecording();
        SpectreState.data = new ReplayData();
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, true));
        AsyncKeyRecorder.FlushFrame();
        bool reset = SpectreState.data.KeyEvents.Count == 1;
        bool final = true;
        foreach (bool hasPrevious in new[] { false, true })
        {
            ADOBase.conductor = null;
            AsyncKeyRecorder.BeginRecording();
            SpectreState.data = new ReplayData();
            double anchor = hasPrevious ? 2 : 0;
            if (hasPrevious) SpectreState.data.KeyEvents.Add(new KeyEvent { KeyCode = 65, SongPosition = anchor });
            int start = SpectreState.data.KeyEvents.Count;
            AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(66, true, 100000000));
            AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(66, false, 105000000));
            AsyncKeyRecorder.FlushFrame(force: true); // 先触发临时缺时钟告警，最终降级仍须另行告警。
            int warnings = UnityEngine.Debug.Warnings;
            int saves = SpectreState.SaveCount;
            SpectreState.LateSaveMode = true;
            Stop();
            var saved = SpectreState.data;
            final &= !SpectreState.is_recording && saved.KeyEvents.Count == start + 2
                && saved.KeyEvents[start].IsPressed && !saved.KeyEvents[start + 1].IsPressed
                && saved.KeyEvents[start].SongPosition == anchor && saved.KeyEvents[start + 1].SongPosition == anchor + 0.5
                && UnityEngine.Debug.Warnings > warnings && SpectreState.PrevKeyState[66] == 0
                && SpectreState.SaveCount == saves + 1 && SpectreState.LastSaved.Count == start + 2
                && SpectreState.LastSaved[start + 1].SongPosition == anchor + 0.5;
            Stop();
            final &= SpectreState.SaveCount == saves + 1 && saved.KeyEvents.Count == start + 2;
            ADOBase.conductor = new scrConductor();
            AsyncKeyRecorder.FlushFrame(force: true);
            AsyncKeyRecorder.BeginRecording();
            SpectreState.data = new ReplayData();
            SpectreState.is_recording = true;
            AsyncKeyRecorder.FlushFrame();
            final &= saved.KeyEvents.Count == start + 2 && SpectreState.data.KeyEvents.Count == 0;
        }
        AsyncKeyRecorder.BeginRecording();
        SpectreState.data = new ReplayData();
        ADOBase.conductor = new scrConductor();
        AsyncKeyRecorder.FlushFrame();
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, true));
        bool joined = false;
        SkyHook.SkyHookEvent.ReadHook = () =>
        {
            SkyHook.SkyHookEvent.ReadHook = null;
            var producer = new System.Threading.Thread(() => AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(66, true)));
            producer.Start();
            joined = producer.Join(2000);
        };
        SpectreState.LateSaveMode = true;
        Stop();
        var pending = typeof(AsyncKeyRecorder).GetField("Pending", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        int PendingCount() => (int)pending.GetType().GetProperty("Count").GetValue(pending);
        bool cutoff = joined && PendingCount() == 0 && SpectreState.LastSaved.Count == 1;
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(67, true));
        cutoff &= PendingCount() == 0;
        AsyncKeyRecorder.BeginRecording();
        SpectreState.is_recording = true;
        SpectreState.data = new ReplayData();
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(66, true));
        AsyncKeyRecorder.FlushFrame();
        cutoff &= SpectreState.data.KeyEvents.Count == 1 && SpectreState.data.KeyEvents[0].KeyCode == 66;
        SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
        SpectreState.data = new ReplayData(); SpectreState.is_recording = true; SpectreState.UseAsyncInput = false;
        KeyboardSimulation.Physical = new byte[256];
        AsyncKeyRecorder.BeginRecording();
        SpectreState.SetGetKeyAsyncEnabled(true);
        var oldListener = SkyHook.SkyHookManager.KeyUpdated.Current;
        var lateDown = new SkyHook.SkyHookEvent(65, true);
        var ready = new System.Threading.ManualResetEventSlim(false);
        var release = new System.Threading.ManualResetEventSlim(false);
        var late = new System.Threading.Thread(() => { ready.Set(); if (release.Wait(2000)) oldListener(lateDown); });
        late.Start(); bool started = ready.Wait(2000);
        KeyboardSimulation.Physical[65] = 128;
        SpectreState.SetGetKeyAsyncEnabled(false);
        KeyboardSimulation.Physical[65] = 0;
        ReplayRecorder.RecordKeyboardState();
        release.Set(); bool lateJoined = late.Join(2000);
        ReplayRecorder.RecordKeyboardState();
        bool handoff = started && lateJoined && PendingCount() == 0 && SpectreState.data.KeyEvents.Count == 2
            && SpectreState.data.KeyEvents[0].IsPressed && !SpectreState.data.KeyEvents[1].IsPressed;
        SpectreState.SetGetKeyAsyncEnabled(true);
        oldListener(new SkyHook.SkyHookEvent(65, true));
        KeyboardSimulation.Physical[66] = 128;
        SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(66, true));
        AsyncKeyRecorder.FlushFrame();
        handoff &= SpectreState.data.KeyEvents.Count == 3 && SpectreState.data.KeyEvents[2].KeyCode == 66;
        SpectreState.SetGetKeyAsyncEnabled(false);
        SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
        SpectreState.data = new ReplayData(); SpectreState.is_recording = true;
        KeyboardSimulation.Physical = new byte[256];
        ADOBase.conductor = null;
        AsyncKeyRecorder.BeginRecording();
        SpectreState.SetGetKeyAsyncEnabled(true);
        var noClockListener = SkyHook.SkyHookManager.KeyUpdated.Current;
        KeyboardSimulation.Physical[65] = 128;
        SpectreState.SetGetKeyAsyncEnabled(false);
        bool missingClock = SpectreState.data.KeyEvents.Count == 0 && SpectreState.PrevKeyState[65] == 0;
        noClockListener(new SkyHook.SkyHookEvent(65, true));
        missingClock &= PendingCount() == 0;
        ADOBase.conductor = new scrConductor();
        ReplayRecorder.RecordKeyboardState();
        ReplayRecorder.RecordKeyboardState();
        missingClock &= SpectreState.data.KeyEvents.Count == 1 && SpectreState.data.KeyEvents[0].IsPressed;
        KeyboardSimulation.Physical[65] = 0;
        ReplayRecorder.RecordKeyboardState();
        missingClock &= SpectreState.data.KeyEvents.Count == 2 && !SpectreState.data.KeyEvents[1].IsPressed;

        // On sync→async enable, the listener is already active while the sync sample runs.
        // If its song clock is unavailable, queued presses must be compared against the last
        // committed synchronous baseline (PrevKeyState), not the uncommitted CurrKeyState.
        SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
        SpectreState.data = new ReplayData(); SpectreState.is_recording = true; SpectreState.UseAsyncInput = false;
        KeyboardSimulation.Physical = new byte[256];
        ADOBase.conductor = null;
        AsyncKeyRecorder.BeginRecording();
        KeyboardSimulation.Physical[67] = 128;
        KeyboardSimulation.SampleHook = () => SkyHook.SkyHookManager.KeyUpdated.Current(
            new SkyHook.SkyHookEvent(67, true));
        SpectreState.SetGetKeyAsyncEnabled(true);
        bool queuedPress = SpectreState.UseAsyncInput && SpectreState.data.KeyEvents.Count == 0
            && SpectreState.PrevKeyState[67] == 0;
        ADOBase.conductor = new scrConductor();
        AsyncKeyRecorder.FlushFrame();
        KeyboardSimulation.Physical[67] = 0;
        SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(67, false));
        AsyncKeyRecorder.FlushFrame();
        queuedPress &= SpectreState.data.KeyEvents.Count == 2
            && SpectreState.data.KeyEvents[0].KeyCode == 67 && SpectreState.data.KeyEvents[0].IsPressed
            && SpectreState.data.KeyEvents[1].KeyCode == 67 && !SpectreState.data.KeyEvents[1].IsPressed;

        // A held key already committed by sync polling needs an async release even if the
        // enable-time clock sample fails; seeding from Prev preserves that baseline.
        SpectreState.SetGetKeyAsyncEnabled(false);
        SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
        SpectreState.data = new ReplayData(); SpectreState.is_recording = true; SpectreState.UseAsyncInput = false;
        KeyboardSimulation.Physical = new byte[256];
        KeyboardSimulation.Physical[68] = 128;
        ADOBase.conductor = new scrConductor();
        AsyncKeyRecorder.BeginRecording();
        ReplayRecorder.RecordKeyboardState();
        bool existingHeld = SpectreState.data.KeyEvents.Count == 1
            && SpectreState.data.KeyEvents[0].IsPressed && SpectreState.PrevKeyState[68] == 128;
        ADOBase.conductor = null;
        SpectreState.SetGetKeyAsyncEnabled(true);
        KeyboardSimulation.Physical[68] = 0;
        SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(68, false));
        ADOBase.conductor = new scrConductor();
        AsyncKeyRecorder.FlushFrame();
        existingHeld &= SpectreState.data.KeyEvents.Count == 2
            && SpectreState.data.KeyEvents[0].IsPressed && !SpectreState.data.KeyEvents[1].IsPressed
            && SpectreState.data.KeyEvents[1].KeyCode == 68;
        // With a valid clock, sync has already committed the sampled DOWN. The queued
        // copy of that edge must not produce a duplicate when async takes over.
        SpectreState.SetGetKeyAsyncEnabled(false);
        SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
        SpectreState.data = new ReplayData(); SpectreState.is_recording = true;
        KeyboardSimulation.Physical = new byte[256];
        ADOBase.conductor = new scrConductor();
        AsyncKeyRecorder.BeginRecording();
        KeyboardSimulation.Physical[69] = 128;
        KeyboardSimulation.SampleHook = () => SkyHook.SkyHookManager.KeyUpdated.Current(
            new SkyHook.SkyHookEvent(69, true));
        SpectreState.SetGetKeyAsyncEnabled(true);
        AsyncKeyRecorder.FlushFrame();
        bool normalClock = SpectreState.data.KeyEvents.Count == 1
            && SpectreState.data.KeyEvents[0].KeyCode == 69 && SpectreState.data.KeyEvents[0].IsPressed;
        KeyboardSimulation.Physical[69] = 0;
        SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(69, false));
        AsyncKeyRecorder.FlushFrame();
        normalClock &= SpectreState.data.KeyEvents.Count == 2 && !SpectreState.data.KeyEvents[1].IsPressed;

        // A release already visible in Curr but not yet committed must still be emitted
        // from the listener queue. Also cover the valid-clock path (exactly one UP).
        bool sampledRelease = true;
        foreach (bool hasClock in new[] { false, true })
        {
            SpectreState.SetGetKeyAsyncEnabled(false);
            SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
            SpectreState.data = new ReplayData(); SpectreState.is_recording = true;
            KeyboardSimulation.Physical = new byte[256];
            ADOBase.conductor = new scrConductor();
            AsyncKeyRecorder.BeginRecording();
            KeyboardSimulation.Physical[70] = 128;
            ReplayRecorder.RecordKeyboardState();
            // Clear the cached async clock too, retaining the last committed sync state.
            AsyncKeyRecorder.BeginRecording();
            if (!hasClock) ADOBase.conductor = null;
            KeyboardSimulation.Physical[70] = 0;
            KeyboardSimulation.SampleHook = () => SkyHook.SkyHookManager.KeyUpdated.Current(
                new SkyHook.SkyHookEvent(70, false));
            SpectreState.SetGetKeyAsyncEnabled(true);
            ADOBase.conductor = new scrConductor();
            AsyncKeyRecorder.FlushFrame();
            AsyncKeyRecorder.FlushFrame();
            sampledRelease &= SpectreState.data.KeyEvents.Count == 2
                && SpectreState.data.KeyEvents[0].KeyCode == 70 && SpectreState.data.KeyEvents[0].IsPressed
                && SpectreState.data.KeyEvents[1].KeyCode == 70 && !SpectreState.data.KeyEvents[1].IsPressed;
        }
        var gaps = new bool[4];
        for (int gap = 0; gap < gaps.Length; gap++)
        {
            SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
            SpectreState.data = new ReplayData(); SpectreState.is_recording = true;
            Application.isFocused = true; SkyHook.AsyncInputManager.isActive = true;
            AudioSettings.dspTime = 10;
            ADOBase.conductor = new scrConductor();
            AsyncKeyRecorder.BeginRecording();
            AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, true));
            AsyncKeyRecorder.FlushFrame();
            if (gap % 2 == 0) Application.isFocused = false;
            else SkyHook.AsyncInputManager.isActive = false;
            if (gap >= 2) ADOBase.conductor = null;
            AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, false));
            AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(66, true));
            AsyncKeyRecorder.FlushFrame();
            bool discarded = PendingCount() == 0 && SpectreState.data.KeyEvents.Count == 1;
            Application.isFocused = true; SkyHook.AsyncInputManager.isActive = true;
            if (gap >= 2)
            {
                AsyncKeyRecorder.FlushFrame();
                discarded &= SpectreState.data.KeyEvents.Count == 1;
            }
            ADOBase.conductor = new scrConductor(); AudioSettings.dspTime = 10.5;
            AsyncKeyRecorder.FlushFrame(); // 恢复时没有新事件，也必须释放断档前已录持键。
            bool released = SpectreState.data.KeyEvents.Count == 2
                && SpectreState.data.KeyEvents[1].KeyCode == 65 && !SpectreState.data.KeyEvents[1].IsPressed
                && SpectreState.PrevKeyState[65] == 0;
            AudioSettings.dspTime = 10.75;
            AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, true));
            AsyncKeyRecorder.FlushFrame();
            AudioSettings.dspTime = 11;
            AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, false));
            AsyncKeyRecorder.FlushFrame();
            var edges = SpectreState.data.KeyEvents;
            gaps[gap] = discarded && released && edges.Count == 4
                && edges.All(e => e.KeyCode == 65) && edges[2].IsPressed && !edges[3].IsPressed
                && edges.Zip(edges.Skip(1), (a, b) => a.SongPosition <= b.SongPosition).All(v => v);
        }
        // 旧局留下断档后直接重开，新局的同步持键种子不能被过期清理释放。
        AsyncKeyRecorder.OnKeyUpdated(new SkyHook.SkyHookEvent(65, true)); AsyncKeyRecorder.FlushFrame();
        Application.isFocused = false; AsyncKeyRecorder.FlushFrame();
        SpectreState.data = new ReplayData(); SpectreState.PrevKeyState = new byte[256];
        SpectreState.PrevKeyState[67] = 128;
        SpectreState.data.KeyEvents.Add(new KeyEvent { KeyCode = 67, IsPressed = true, SongPosition = 0 });
        AsyncKeyRecorder.BeginRecording(); AsyncKeyRecorder.SeedHeldState(SpectreState.PrevKeyState);
        Application.isFocused = true; AsyncKeyRecorder.FlushFrame();
        bool gapReset = SpectreState.data.KeyEvents.Count == 1 && SpectreState.PrevKeyState[67] == 128;
        bool reenableKeepsPending = true;
        foreach (bool restoreBeforeEnable in new[] { false, true })
        {
            SpectreState.SetGetKeyAsyncEnabled(false);
            SpectreState.UseAsyncInput = false;
            SpectreState.data = new ReplayData();
            SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
            KeyboardSimulation.Physical = new byte[256];
            ADOBase.conductor = null;
            AsyncKeyRecorder.BeginRecording();
            SpectreState.SetGetKeyAsyncEnabled(true);
            SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(65, true));
            SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(65, false));
            SpectreState.SetGetKeyAsyncEnabled(false);
            reenableKeepsPending &= PendingCount() == 2 && SpectreState.data.KeyEvents.Count == 0;
            if (restoreBeforeEnable) ADOBase.conductor = new scrConductor();
            SpectreState.SetGetKeyAsyncEnabled(true);
            ADOBase.conductor = new scrConductor();
            AsyncKeyRecorder.FlushFrame(); AsyncKeyRecorder.FlushFrame();
            var clicks = SpectreState.data.KeyEvents;
            reenableKeepsPending &= clicks.Count == 2 && clicks.All(e => e.KeyCode == 65)
                && clicks[0].IsPressed && !clicks[1].IsPressed && PendingCount() == 0;
        }
        var handoffOrder = new bool[6];
        for (int scenario = 0; scenario < handoffOrder.Length; scenario++)
        {
            SpectreState.SetGetKeyAsyncEnabled(false);
            bool initiallyHeld = scenario >= 3;
            int delivery = scenario % 3;
            SpectreState.data = new ReplayData();
            SpectreState.PrevKeyState = new byte[256]; SpectreState.CurrKeyState = new byte[256];
            KeyboardSimulation.Physical = new byte[256];
            if (initiallyHeld)
            {
                SpectreState.PrevKeyState[71] = 128;
                SpectreState.data.KeyEvents.Add(new KeyEvent { KeyCode = 71, IsPressed = true, SongPosition = -1 });
            }
            ADOBase.conductor = new scrConductor { dspTimeSong = 9, songposition_minusi = 1 };
            AudioSettings.dspTime = 10;
            AsyncKeyRecorder.BeginRecording();
            KeyboardSimulation.Physical[71] = (byte)(initiallyHeld ? 0 : 128);
            SkyHook.SkyHookEvent[] raw = null;
            KeyboardSimulation.SampleHook = () =>
            {
                long now = DateTime.Now.Ticks;
                raw = new[] {
                    new SkyHook.SkyHookEvent(71, !initiallyHeld, now - 300000),
                    new SkyHook.SkyHookEvent(71, initiallyHeld, now - 200000),
                    new SkyHook.SkyHookEvent(71, !initiallyHeld, now - 100000) };
                if (delivery == 0)
                    foreach (var edge in raw) SkyHook.SkyHookManager.KeyUpdated.Current(edge);
            };
            SpectreState.SetGetKeyAsyncEnabled(true);
            AsyncKeyRecorder.FlushFrame();
            if (delivery == 2)
            {
                // 先处理采样之后的真实边沿，随后旧 tick 才交付。修正旧小段不能
                // 把这条新边沿更新后的 Prev/Held 重置回交接快照。
                AudioSettings.dspTime = 10.1;
                SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(71, initiallyHeld));
                AsyncKeyRecorder.FlushFrame();
            }
            if (delivery != 0)
                foreach (var edge in raw) SkyHook.SkyHookManager.KeyUpdated.Current(edge);
            AsyncKeyRecorder.FlushFrame(); AsyncKeyRecorder.FlushFrame();
            var all = SpectreState.data.KeyEvents;
            int prefix = initiallyHeld ? 1 : 0;
            var pulse = all.Skip(prefix).Take(3).ToArray();
            bool correct = all.Count == prefix + 3 + (delivery == 2 ? 1 : 0)
                && pulse.Length == 3 && pulse[0].IsPressed == !initiallyHeld
                && pulse[1].IsPressed == initiallyHeld && pulse[2].IsPressed == !initiallyHeld
                && pulse.All(e => e.KeyCode == 71) && pulse[2].SongPosition < 1
                && Math.Abs(pulse[1].SongPosition - pulse[0].SongPosition - 0.01) < 0.000001
                && Math.Abs(pulse[2].SongPosition - pulse[1].SongPosition - 0.01) < 0.000001
                && all.Zip(all.Skip(1), (a, b) => a.SongPosition <= b.SongPosition).All(v => v);
            bool expectedHeld = delivery == 2 ? initiallyHeld : !initiallyHeld;
            var held = (bool[])typeof(AsyncKeyRecorder).GetField("HeldAtLastEdge", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            correct &= held[71] == expectedHeld && ((SpectreState.PrevKeyState[71] & 0x80) != 0) == expectedHeld;

            // 没有原始回调时仍须补记采样差异；新局不能沿用上一次的交接历史。
            SpectreState.SetGetKeyAsyncEnabled(false);
            SpectreState.data = new ReplayData(); SpectreState.PrevKeyState = new byte[256];
            SpectreState.CurrKeyState = new byte[256]; KeyboardSimulation.Physical = new byte[256];
            KeyboardSimulation.Physical[72] = 128;
            AsyncKeyRecorder.BeginRecording();
            SpectreState.SetGetKeyAsyncEnabled(true);
            AsyncKeyRecorder.FlushFrame();
            correct &= SpectreState.data.KeyEvents.Count == 1 && SpectreState.data.KeyEvents[0].KeyCode == 72
                && SpectreState.data.KeyEvents[0].IsPressed;
            handoffOrder[scenario] = correct;
        }
        SpectreState.SetGetKeyAsyncEnabled(false);
        SpectreState.data = new ReplayData(); SpectreState.PrevKeyState = new byte[256];
        SpectreState.CurrKeyState = new byte[256]; KeyboardSimulation.Physical = new byte[256];
        SpectreState.data.KeyEvents.Add(new KeyEvent { KeyCode = 65, IsPressed = false, SongPosition = 1 });
        ADOBase.conductor = new scrConductor { dspTimeSong = 9, songposition_minusi = 1 };
        AudioSettings.dspTime = 10;
        AsyncKeyRecorder.BeginRecording();
        KeyboardSimulation.Physical[65] = 128;
        SpectreState.SetGetKeyAsyncEnabled(true);
        AsyncKeyRecorder.FlushFrame();
        KeyboardSimulation.Physical[65] = 0;
        ReplayRecorder.RecordKeyboardState(forceSync: true); // 同歌曲时间的后续同步UP。
        long handoffTick = (long)typeof(AsyncKeyRecorder).GetField("synchronousHandoff", BindingFlags.Static | BindingFlags.NonPublic)
            .GetValue(null).GetType().GetField("SampleTicks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(
                typeof(AsyncKeyRecorder).GetField("synchronousHandoff", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
        SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(65, true, handoffTick));
        AsyncKeyRecorder.FlushFrame();
        var sameTimeA = SpectreState.data.KeyEvents.Where(e => e.KeyCode == 65).ToArray();
        bool sameTime = SpectreState.data.KeyEvents.Count == 3 && sameTimeA.Length == 3
            && !sameTimeA[0].IsPressed && sameTimeA[0].SongPosition == 1
            && sameTimeA[1].IsPressed && !sameTimeA[2].IsPressed
            && sameTimeA[1].SongPosition == sameTimeA[2].SongPosition && SpectreState.PrevKeyState[65] == 0;

        var sampleRace = new bool[2];
        for (int delivery = 0; delivery < sampleRace.Length; delivery++)
        {
            SpectreState.SetGetKeyAsyncEnabled(false);
            SpectreState.data = new ReplayData(); SpectreState.PrevKeyState = new byte[256];
            SpectreState.CurrKeyState = new byte[256]; KeyboardSimulation.Physical = new byte[256];
            ADOBase.conductor = new scrConductor { dspTimeSong = 9, songposition_minusi = 1 };
            AudioSettings.dspTime = 10;
            AsyncKeyRecorder.BeginRecording();
            SkyHook.SkyHookEvent inSample = null;
            KeyboardSimulation.SampleHook = () =>
            {
                // Physical已经拷到Curr，此后发生DOWN：Curr仍up，raw才是新事实。
                KeyboardSimulation.Physical[73] = 128;
                inSample = new SkyHook.SkyHookEvent(73, true);
                if (delivery == 0) SkyHook.SkyHookManager.KeyUpdated.Current(inSample);
            };
            SpectreState.SetGetKeyAsyncEnabled(true);
            AsyncKeyRecorder.FlushFrame();
            if (delivery == 1) SkyHook.SkyHookManager.KeyUpdated.Current(inSample);
            AudioSettings.dspTime = 10.1;
            SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(73, false));
            AsyncKeyRecorder.FlushFrame(); AsyncKeyRecorder.FlushFrame();
            var click = SpectreState.data.KeyEvents;
            sampleRace[delivery] = click.Count == 2 && click[0].IsPressed && !click[1].IsPressed
                && click.All(e => e.KeyCode == 73) && click[0].SongPosition <= click[1].SongPosition
                && SpectreState.PrevKeyState[73] == 0;
        }
        var crossBatch = new bool[2];
        var laterSample = new bool[2];
        var gapHandoff = new bool[2];
        for (int direction = 0; direction < 2; direction++)
        for (int sync = 0; sync < 3; sync++)
        {
            SpectreState.SetGetKeyAsyncEnabled(false);
            bool initiallyHeld = direction == 1;
            SpectreState.data = new ReplayData(); SpectreState.PrevKeyState = new byte[256];
            SpectreState.CurrKeyState = new byte[256]; KeyboardSimulation.Physical = new byte[256];
            if (initiallyHeld)
            {
                SpectreState.PrevKeyState[74] = 128; KeyboardSimulation.Physical[74] = 128;
                SpectreState.data.KeyEvents.Add(new KeyEvent { KeyCode = 74, IsPressed = true, SongPosition = 0.5 });
            }
            ADOBase.conductor = new scrConductor { dspTimeSong = 9, songposition_minusi = 1 };
            AudioSettings.dspTime = 10;
            AsyncKeyRecorder.BeginRecording();
            SkyHook.SkyHookEvent inSample = null;
            KeyboardSimulation.SampleHook = () =>
            {
                KeyboardSimulation.Physical[74] = (byte)(initiallyHeld ? 0 : 128);
                inSample = new SkyHook.SkyHookEvent(74, !initiallyHeld);
            };
            SpectreState.SetGetKeyAsyncEnabled(true);
            AsyncKeyRecorder.FlushFrame();
            KeyboardSimulation.Physical[74] = (byte)(initiallyHeld ? 128 : 0);
            AudioSettings.dspTime = 10.1;
            ADOBase.conductor.songposition_minusi = 1.1;
            if (sync == 0)
            {
                SkyHook.SkyHookManager.KeyUpdated.Current(new SkyHook.SkyHookEvent(74, initiallyHeld));
                AsyncKeyRecorder.FlushFrame();
            }
            else if (sync == 1) ReplayRecorder.RecordKeyboardState(forceSync: true);
            else
            {
                Application.isFocused = false; AsyncKeyRecorder.FlushFrame();
                KeyboardSimulation.Physical[74] = 0;
                Application.isFocused = true; AsyncKeyRecorder.FlushFrame();
            }
            SkyHook.SkyHookManager.KeyUpdated.Current(inSample);
            AsyncKeyRecorder.FlushFrame(); AsyncKeyRecorder.FlushFrame();
            var click = SpectreState.data.KeyEvents.Skip(initiallyHeld ? 1 : 0).ToArray();
            var held = (bool[])typeof(AsyncKeyRecorder).GetField("HeldAtLastEdge", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            bool correct = click.Length == 2 && click.All(e => e.KeyCode == 74)
                && click[0].IsPressed == !initiallyHeld && click[1].IsPressed == initiallyHeld
                && click[0].SongPosition <= click[1].SongPosition
                && held[74] == initiallyHeld && ((SpectreState.PrevKeyState[74] & 0x80) != 0) == initiallyHeld;
            if (sync == 0) crossBatch[direction] = correct;
            else if (sync == 1) laterSample[direction] = correct;
            else gapHandoff[direction] = click.Length == (initiallyHeld ? 1 : 2)
                && click.All(e => e.KeyCode == 74) && !click[^1].IsPressed
                && (initiallyHeld || click[0].IsPressed) && !held[74] && SpectreState.PrevKeyState[74] == 0
                && click.Zip(click.Skip(1), (a, b) => a.SongPosition <= b.SongPosition).All(v => v);
        }
        Stop();
        SpectreState.data = new ReplayData();
        bool releasedHandoff = typeof(AsyncKeyRecorder).GetField("synchronousHandoff", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == null;
        return new[] { cached, preserved, reset, final, cutoff, handoff, missingClock,
            queuedPress && existingHeld && normalClock && sampledRelease,
            gaps[0], gaps[1], gaps[2], gaps[3], gapReset, reenableKeepsPending,
            handoffOrder[0], handoffOrder[1], handoffOrder[2], handoffOrder[3], handoffOrder[4], handoffOrder[5],
            sameTime, sampleRace[0], sampleRace[1], crossBatch[0], crossBatch[1], laterSample[0], laterSample[1],
            gapHandoff[0], gapHandoff[1], releasedHandoff };
    }
}
namespace SpectreReplay
{
    public static class SpectreState
    {
        public static bool is_recording, IgnoreMarkFail, LateSaveMode, UseAsyncInput;
        public static byte[] CurrKeyState = new byte[256];
        private static UnityEngine.Events.UnityAction<SkyHook.SkyHookEvent> _asyncKeyListener;
        __TOGGLE__
        public static ReplayData data;
        public static byte[] PrevKeyState = new byte[256];
        public static int[] HitMarginCounts = new int[2];
        public static int SaveCount;
        public static List<KeyEvent> LastSaved;
        public static double ComputePercentXacc() => 100;
        public static string CreateFileName() => "saved";
        public static void SaveFile() { SaveCount++; LastSaved = new List<KeyEvent>(data.KeyEvents); }
        public static void NotifySaveSkipped(string message, string reason) { }
    }
    public class ReplayData
    {
        public List<KeyEvent> KeyEvents = new List<KeyEvent>();
        public int? EndTile, CheckpointsUsed, MaximumUsedKeys;
        public string JudgmentList, SceneName = "scnEditor", FileName, KeybdSoundFileName, KeybdSoundHash;
        public double PercentXacc, KeybdSoundStop;
        public double? KeybdSoundStartTick;
    }
    public class KeyEvent { public ushort KeyCode; public bool IsPressed; public double SongPosition; }
    static class Settings { public static bool GetDataFromAsyncInput = true, KeybdSoundRecordActive, DontSaveWhenMiss; }
    static class LocalizationManager { public static string GetLocalizedText(string key) => key; }
    class AudioRecorder
    {
        public static AudioRecorder Instance = new AudioRecorder();
        public bool IsRecording;
        public double LastStop;
        public string StopRecording() => "audio";
    }
}
namespace SpectreReplay.Features.Replay
{
    static class ManagedInput { public static bool IsModHotkey(int key) => false; }
    static class ReplayRecorder
    {
        static void RecordCursor() { }
        __POLL__
    }
}
namespace SpectreReplay
{
    static class KeyboardSimulation
    {
        public static byte[] Physical = new byte[256];
        public static Action SampleHook;
        public static void SampleKeyState(byte[] state)
        {
            Array.Copy(Physical, state, 256);
            var hook = SampleHook;
            SampleHook = null;
            hook?.Invoke();
        }
    }
}
namespace SpectreReplay.Features.Compat { static class HitMarginCompat { public static int FailCount(int[] values) => 0; } }
namespace UnityEngine
{
    static class Application { public static bool isFocused = true; }
    static class AudioSettings { public static double dspTime = 10; }
    static class Debug { public static int Warnings; public static void LogWarning(string value) { Warnings++; } public static void Log(string value) { } }
}
namespace UnityEngine.Events { public delegate void UnityAction<T>(T value); }
namespace SkyHook
{
    sealed class Listener
    {
        public UnityEngine.Events.UnityAction<SkyHookEvent> Current;
        public void AddListener(UnityEngine.Events.UnityAction<SkyHookEvent> listener) { Current = listener; }
        public void RemoveListener(UnityEngine.Events.UnityAction<SkyHookEvent> listener) { if (Current == listener) Current = null; }
    }
    static class SkyHookManager { public static Listener KeyUpdated = new Listener(); }
    enum KeyLabel { Key, MouseLeft, MouseMiddle, MouseRight, MouseX1, MouseX2 }
    enum EventType { KeyPressed, KeyReleased }
    sealed class SkyHookEvent
    {
        internal ushort Key; internal KeyLabel Label; internal EventType Type; long ticks;
        internal SkyHookEvent(ushort key, bool pressed, long? eventTicks = null) { Key = key; Type = pressed ? EventType.KeyPressed : EventType.KeyReleased; ticks = eventTicks ?? DateTime.Now.Ticks; }
        internal static Action ReadHook;
        internal long GetTimeInTicks() { ReadHook?.Invoke(); return ticks; }
    }
    static class AsyncInputManager { internal static bool isActive = true; }
}
class Song { public double pitch = 1; }
class scrConductor { public Song song = new Song(); public double dspTimeSong = 10, addoffset, songposition_minusi = 1; public static double calibration_i; }
class scrController
{
    public static scrController instance;
    public static int checkpointsUsed;
    public Player playerOne;
    public int currentSeqID, maximumUsedKeys;
}
class Player { public Tracker marginTracker; }
class Tracker { public int[] hitMarginsCount; }
static class ADOBase { public static scrConductor conductor; public static scrController controller; }
""";
}
