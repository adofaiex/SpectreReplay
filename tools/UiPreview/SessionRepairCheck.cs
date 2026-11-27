using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// 真实源码行为夹具：方法体来自当前产品源码，只替换 Unity/SkyHook/Steam 边界。
// Driver 注册与 mutate 锚点由父会话统一维护；本文件只提供 Run()。
internal static class SessionRepairCheck
{
    internal static int Run(string projectDir)
    {
        try
        {
            var state = Type(projectDir, "State/SpectreState.cs", "PlayActions");
            var switchPlay = Method(state, "SwitchToPlayMode");
            var switchRecord = Method(state, "SwitchToRecordMode");
            var api = Parse(projectDir, "State/SpectreAPI.cs");
            var apiType = api.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(t => t.Identifier.ValueText == "SpectreAPI");
            var loadedHelper = apiType.Members.OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == "HasPlayableReplayData");
            var hasLoaded = apiType.Members.OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == "HasLoadedReplay");
            var info = Method(apiType, "GetLoadedReplayInfo");
            var managed = Type(projectDir, "Features/Replay/Runtime/ManagedInput.cs", "ManagedInput");
            var inputMembers = managed.Members.Where(m => m is FieldDeclarationSyntax f &&
                (f.Declaration.Variables.Any(v => new[] { "Held", "SourceHeld", "SourceVk", "HeldSourceCount", "PressedFrame", "PressCountFrame", "ReleasedFrame", "_heldCount", "_loggedFirstInjection", "_takeoverFrame", "_takeover" }.Contains(v.Identifier.ValueText))))
                .Select(m => m.ToString());
            var inputMethods = new[] { "Note", "TrackSource", "Clear", "SnapshotHeld", "IsHeldVk", "Count" }
                .Select(name => managed.Members.OfType<MethodDeclarationSyntax>()
                    .Single(m => m.Identifier.ValueText == name).ToString());
            var asyncState = Parse(projectDir, "State/SpectreState.cs");
            var asyncStateType = asyncState.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(t => t.Identifier.ValueText == "SpectreState");
            var asyncToggle = Method(asyncStateType, "SetGetKeyAsyncEnabled");
            var cloud = Type(projectDir, "Features/Cloud/CloudReplay.cs", "CloudReplay");
            var cloudApply = Method(cloud, "ApplyRestoredConfig");
            var recordPage = Type(projectDir, "Pages/RecordPage.cs", "Pages");
            var recordToggleCall = Method(recordPage, "DrawRecord").DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(call => call.Expression is MemberAccessExpressionSyntax member
                    && member.Name.Identifier.ValueText == "Row")
                .Single(call => call.ArgumentList.Arguments.Count >= 3
                    && call.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
                    && literal.Token.ValueText == "record.recording");
            var recordToggle = recordToggleCall.ArgumentList.Arguments[2].Expression
                as SimpleLambdaExpressionSyntax
                ?? throw new InvalidOperationException("recording toggle callback is not a simple lambda");

            string source = Fixture
                .Replace("__SWITCH_PLAY__", switchPlay.ToString(), StringComparison.Ordinal)
                .Replace("__SWITCH_RECORD__", switchRecord.ToString(), StringComparison.Ordinal)
                .Replace("__API_HELPER__", loadedHelper.ToString().Replace("SpectreState.", "", StringComparison.Ordinal), StringComparison.Ordinal)
                .Replace("__API_HAS__", hasLoaded.ToString().Replace("SpectreState.", "", StringComparison.Ordinal), StringComparison.Ordinal)
                .Replace("__API_INFO__", info.ToString().Replace("SpectreState.", "", StringComparison.Ordinal), StringComparison.Ordinal)
                .Replace("__INPUT_FIELDS__", string.Join("\n", inputMembers), StringComparison.Ordinal)
                .Replace("__INPUT_METHODS__", string.Join("\n", inputMethods), StringComparison.Ordinal)
                .Replace("__ASYNC_TOGGLE__", asyncToggle.ToString(), StringComparison.Ordinal)
                .Replace("__CLOUD_APPLY__", cloudApply.ToString(), StringComparison.Ordinal)
                .Replace("__RECORD_TOGGLE__", "private static void RecordToggle(bool v) "
                    + recordToggle.Body.ToFullString(), StringComparison.Ordinal);

            string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            if (string.IsNullOrEmpty(trusted)) throw new InvalidOperationException("runtime reference set missing");
            var references = trusted.Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("SessionRepairFixture_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success)
            {
                throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));
            }
            var fixture = Assembly.Load(image.ToArray()).GetType("SessionRepairFixture");
            var lines = (string[])fixture.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, null);
            foreach (string line in lines) Console.WriteLine("  " + line);
            return lines.Count(line => line.StartsWith("✗", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ 会话修复真实源码回归无法执行：" + (ex.InnerException ?? ex).Message);
            return 1;
        }
    }

    private static CompilationUnitSyntax Parse(string projectDir, string relativePath)
        => CSharpSyntaxTree.ParseText(SrcUtil.Source(projectDir, relativePath)).GetCompilationUnitRoot();

    private static ClassDeclarationSyntax Type(string projectDir, string relativePath, string name)
        => Parse(projectDir, relativePath).DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == name);

    private static MethodDeclarationSyntax Method(ClassDeclarationSyntax type, string name)
        => type.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == name);

    private const string Fixture = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using SpectreReplay.Features.VideoRender;

static class SessionRepairFixture
{
    public static readonly List<string> Events = new List<string>();
    static bool PlayMode;
    static bool RecordMode;
    static bool is_playing;
    static bool is_recording;
    static bool PendingSave;
    static bool LateSaveMode;
    static int PlayIndex;
    static int KeyEventIndex;
    static bool IgnoreMarkFail;
    static bool HitLocked;
    static bool IsFullRun;
    static bool UseAsyncInput;
    static bool ReplayOn;
    static bool GetDataFromAsyncInput = true;
    static bool ApiBlockPlaying;
    static bool _configApplyingRestore;
    static bool CachedDifficulty;
    static byte[] CurrKeyState = new byte[256];
    static byte[] PrevKeyState = new byte[256];
    static UnityEngine.Events.UnityAction<SkyHook.SkyHookEvent> _asyncKeyListener;
    static ReplayData data;

    public static void StopPlaying()
    {
        if (!is_playing) return;
        Events.Add("play-stop");
        is_playing = false;
    }

    public static void StopRecording()
    {
        if (!is_recording) return;
        Events.Add("record-save");
        is_recording = false;
    }

    static class PlayActions
    {
        public static void StopPlaying() => SessionRepairFixture.StopPlaying();
        public static void StopRecording() => SessionRepairFixture.StopRecording();
    }

    __SWITCH_PLAY__
    __SWITCH_RECORD__

    __API_HELPER__
    __API_HAS__
    __API_INFO__

    __INPUT_FIELDS__
    __INPUT_METHODS__

    private static bool Matches(ButtonState state, int vk, int frame)
    {
        switch (state)
        {
            case ButtonState.WentDown: return PressedFrame[vk] == frame;
            case ButtonState.IsDown: return Held[vk];
            case ButtonState.WentUp: return ReleasedFrame[vk] == frame;
            default: return false;
        }
    }

    public static void SamplePollingState()
    {
        KeyboardSimulation.SampleKeyState(CurrKeyState);
        Array.Copy(CurrKeyState, PrevKeyState, CurrKeyState.Length);
    }

    __ASYNC_TOGGLE__

    __CLOUD_APPLY__
    __RECORD_TOGGLE__
    static void Commit() { }

    static void Check(List<string> lines, bool ok, string label)
        => lines.Add((ok ? "✓ " : "✗ ") + label);

    public static string[] Run()
    {
        var lines = new List<string>();
        Events.Clear();
        is_playing = true;
        is_recording = true;
        SwitchToPlayMode();
        Check(lines, Events.SequenceEqual(new[] { "play-stop", "record-save" })
            && PlayMode && !RecordMode && !is_playing && !is_recording,
            "SR01 切播放模式先收尾旧回放与录制，再进入播放态");

        Events.Clear();
        PlayMode = true;
        RecordMode = false;
        is_playing = true;
        is_recording = true;
        SwitchToRecordMode();
        Check(lines, Events.SequenceEqual(new[] { "play-stop", "record-save" })
            && !PlayMode && RecordMode && !is_playing && !is_recording,
            "SR02 切录制模式先收尾旧会话，再进入录制态");

        data = null;
        PlayMode = false;
        is_recording = false;
        Check(lines, !HasLoadedReplay && GetLoadedReplayInfo() == null,
            "SR03 空 data 且非播放模式不算已装载");
        data = new ReplayData { HasRequiredMetadata = true };
        PlayMode = false;
        is_recording = false;
        bool outsidePlayback = !HasLoadedReplay && GetLoadedReplayInfo() == null;
        PlayMode = true;
        is_recording = true;
        Check(lines, outsidePlayback && !HasLoadedReplay && GetLoadedReplayInfo() == null,
            "SR04 录制中的有效元数据不算已装载");
        PlayMode = true;
        is_recording = false;
        Check(lines, HasLoadedReplay && GetLoadedReplayInfo() != null,
            "SR05 播放模式中的完整数据才算已装载");
        data.HasRequiredMetadata = false;
        Check(lines, !HasLoadedReplay && GetLoadedReplayInfo() == null,
            "SR06 播放模式空壳数据仍不算已装载");

        Clear();
        Events.Clear();
        UnityEngine.Time.frameCount = 7;
        bool first = Note(67, true, 1, out bool edge1, out ushort target1);
        bool duplicate = Note(67, true, 1, out bool duplicateEdge, out _);
        bool second = Note(67, true, 2, out bool edge2, out ushort target2);
        bool firstUp = Note(67, false, 1, out bool edge3, out ushort target3);
        bool heldAfterFirstUp = IsHeldVk(67);
        bool secondUp = Note(67, false, 2, out bool edge4, out ushort target4);
        Check(lines, first && second && firstUp && secondUp
            && !duplicate && !duplicateEdge && heldAfterFirstUp
            && edge1 && !edge2 && !edge3 && edge4
            && IsHeldVk(67) == false && Count(ButtonState.WentDown) == 2
            && Events.SequenceEqual(new[] { "down:67", "up:67" }),
            "SR07 同目标映射按来源键聚合，重复down幂等，最后up才广播抬起且保留两次按下计数");

        Events.Clear();
        Clear();
        UseAsyncInput = false;
        Events.Clear();
        AsyncKeyRecorder.Seeded = false;
        AsyncKeyRecorder.Flushed = false;
        AsyncKeyRecorder.Discarded = 0;
        SkyHook.SkyHookManager.KeyUpdated.Added = SkyHook.SkyHookManager.KeyUpdated.Removed = 0;
        SetGetKeyAsyncEnabled(true);
        bool enabled = UseAsyncInput && SkyHook.SkyHookManager.KeyUpdated.Added == 1
            && AsyncKeyRecorder.Seeded && KeyboardSimulation.Samples == 1
            && AsyncKeyRecorder.Discarded == 1;
        is_recording = true;
        SetGetKeyAsyncEnabled(false);
        Check(lines, enabled && !UseAsyncInput
            && SkyHook.SkyHookManager.KeyUpdated.Removed == 1
            && AsyncKeyRecorder.Flushed && AsyncKeyRecorder.Discarded == 1
            && KeyboardSimulation.Samples == 2 && PrevKeyState[65] == 128,
            "SR08 异步输入开关完整交接监听、队列和基线");

        ConfigManager.Loaded = Settings.Checked = PatchShared.SpectrePatchHub.Refreshed = false;
        SpectreState.AsyncSet = false;
        Events.Clear();
        is_playing = is_recording = true;
        bool restored = ApplyRestoredConfig("fixture.json");
        Check(lines, restored && ConfigManager.Loaded && Settings.Checked
            && SpectreState.AsyncSet && PatchShared.SpectrePatchHub.Refreshed
            && Events.SequenceEqual(new[] { "play-stop", "record-save", "config-load" }),
            "SR09 云配置恢复在归一化后刷新异步输入与补丁");

        Events.Clear();
        VideoRender.CaptureInProgress = true;
        ReplayOn = true; is_playing = is_recording = true;
        RecordToggle(false);
        Check(lines, !ReplayOn && !is_playing && !is_recording
            && Events.SequenceEqual(new[] { "render-cancel", "play-stop", "record-save" }),
            "SR10 ReplayOffDuringCapture: 真实总开关先取消捕获再停止录放，不能误报自然结束");

        Events.Clear();
        VideoRender.CaptureInProgress = true;
        is_playing = is_recording = true;
        restored = ApplyRestoredConfig("fixture.json");
        Check(lines, restored && !is_playing && !is_recording
            && Events.SequenceEqual(new[] { "render-cancel", "play-stop", "record-save", "config-load" }),
            "SR11 CloudRestoreDuringCapture: 真实云配置中断先取消捕获再保存旧会话并换设置");
        VideoRender.CaptureInProgress = false;
        return lines.ToArray();
    }
}

class ReplayData
{
    public bool HasRequiredMetadata;
    public string SongName;
    public string ArtistName;
    public string JudgeMode;
    public string InternalLevelName;
    public string ModVersion;
    public int? StartTile, EndTile;
    public double? Bpm, PlaybackSpeed;
    public List<HitSnapshot> HitSnapshots = new List<HitSnapshot>();
    public List<KeyEvent> KeyEvents = new List<KeyEvent>();
}
class HitSnapshot { }
class KeyEvent { }
class ReplayInfo
{
    public ReplayInfo(string a, string b, string c, string d, string e, int f, int g,
        double h, double i, int j, int k) { }
}
enum ButtonState { WentDown, IsDown, WentUp }
namespace UnityEngine
{
    public static class Time { public static int frameCount; }
    public static class Debug { public static void Log(string value) { } public static void LogWarning(string value) { } }
}
namespace UnityEngine.Events { public delegate void UnityAction<T>(T value); }
namespace SkyHook
{
    public class SkyHookEvent { }
    public sealed class Listener
    {
        public int Added, Removed;
        public void AddListener(UnityEngine.Events.UnityAction<SkyHookEvent> listener) { Added++; }
        public void RemoveListener(UnityEngine.Events.UnityAction<SkyHookEvent> listener) { Removed++; }
    }
    public static class SkyHookManager { public static Listener KeyUpdated = new Listener(); }
}
static class AsyncKeyRecorder
{
    public static bool Seeded, Flushed;
    public static int Discarded;
    public static void OnKeyUpdated(SkyHook.SkyHookEvent value) { }
    public static UnityEngine.Events.UnityAction<SkyHook.SkyHookEvent> CreateListener() => OnKeyUpdated;
    public static void PauseListener() { }
    public static void SeedHeldState(byte[] value) { Seeded = true; }
    public static void FlushFrame(bool force = false) { Flushed = true; }
    public static void DiscardPending() { Discarded++; }
}
static class ReplayRecorder { public static bool RecordKeyboardState(bool forceSync = false, bool reconcileAsyncHandoff = false) { SessionRepairFixture.SamplePollingState(); return true; } }
static class KeyboardSimulation { public static int Samples; public static void SampleKeyState(byte[] value) { Samples++; value[65] = 128; } }
namespace API
{
    public static class SpectreAPI
    {
        public static void RaiseReplayKeyChanged(ushort key, bool pressed)
            => SessionRepairFixture.Events.Add((pressed ? "down:" : "up:") + key);
    }
}
namespace SpectreReplay.Features.VideoRender
{
    static class VideoRender
    {
        public static bool CaptureInProgress;
        public static void CancelRender() => SessionRepairFixture.Events.Add("render-cancel");
    }
}
// 引擎/UI 边界只占位；配置应用与中断顺序仍由完整真实方法执行。
namespace SpectreReplay.Features.EffectRemover
{
    static class EffectRemover
    {
        public static void RestoreOriginalLevelData() { }
        public static void RestoreEditorSaveButtons() { }
        public static void RefreshEditorSaveButtons() { }
    }
}
namespace Ui { static class OnGui { public static void RefreshAfterConfigRestore() { } } }
static class ConfigManager { public static bool Loaded; public static bool TryLoadConfigs(string path, out string error) { error = null; Loaded = true; SessionRepairFixture.Events.Add("config-load"); return true; } }
static class Settings { public static bool Checked, EffectRemoverOn; public static bool ReplayOn = true, GetDataFromAsyncInput = true; public static void check(string path) { Checked = true; } }
class Mod { public string Path = "fixture"; }
static class Main { public static Mod mod = new Mod(); }
static class PatchShared
{
    public static class SpectrePatchHub { public static bool Refreshed; public static void Refresh() { Refreshed = true; } }
}
static class SpectreState
{
    public static class PlayActions
    {
        public static void StopPlaying() => SessionRepairFixture.StopPlaying();
        public static void StopRecording() => SessionRepairFixture.StopRecording();
    }
    public static bool AsyncSet;
    public static void SetGetKeyAsyncEnabled(bool value) { AsyncSet = value; }
}
""";
}
