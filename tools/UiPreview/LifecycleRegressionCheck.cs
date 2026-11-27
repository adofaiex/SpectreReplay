using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// 从当前产品源码提取方法/语句，替换的仅是 Unity、文件和 OS 边界。
// 编译器来自本机 SDK；不会加载游戏程序集、启动进程或发送真实按键。
internal static class LifecycleRegressionCheck
{
    internal static int Run()
    {
        try
        {
            var playbackType = Type("Features/Replay/Patches/Patch_Playback.cs", "Patch_PlayerControlUpdate");
            var playback = playbackType.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Postfix");
            string recording = string.Join("\n", playback.Body.Statements.TakeWhile(s =>
                s is not IfStatementSyntax condition || condition.Condition.ToString() != "!is_playing"));
            var recorder = Method("Features/Replay/Runtime/ReplayRecorder.cs", "ReplayRecorder", "Stop");
            string recordedAccuracy = string.Join("\n", recorder.Body.Statements
                .SkipWhile(s => !s.ToString().StartsWith("data.CheckpointsUsed =", StringComparison.Ordinal))
                .TakeWhile(s => !s.ToString().StartsWith("int peak =", StringComparison.Ordinal)));
            var info = Type("Features/Replay/Data/ReplayFileInfo.cs", "ReplayFileInfo").Members
                .OfType<MethodDeclarationSyntax>().Single(m => m.Body?.Statements.Any(s =>
                    s.ToString().StartsWith("double recomputed =", StringComparison.Ordinal)) == true);
            string infoAccuracy = string.Join("\n", info.Body.Statements.SkipWhile(s =>
                !s.ToString().StartsWith("double recomputed =", StringComparison.Ordinal)));
            var managed = Type("Features/Replay/Runtime/ManagedInput.cs", "ManagedInput");
            string hotkeyMembers = string.Join("\n", managed.Members.Where(m =>
                m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => v.Identifier.ValueText.StartsWith("_hot", StringComparison.Ordinal))
                || m is MethodDeclarationSyntax method && new[] { "EnsureHotkeyCache", "IsModHotkey", "ToNativeVk" }.Contains(method.Identifier.ValueText)));
            var state = Type("State/SpectreState.cs", "SpectreState");
            string blocked = state.Members.OfType<FieldDeclarationSyntax>().Single(f =>
                f.Declaration.Variables.Any(v => v.Identifier.ValueText == "BlockedKeys")).ToString();
            var render = Type("Features/VideoRender/Pipeline/VideoRenderJob.cs", "VideoRenderJob");
            var prepareBranch = render.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Phases")
                .DescendantNodes().OfType<IfStatementSyntax>().Single(i =>
                    i.Condition.ToString().Contains("ADOBase.editor.floors != null", StringComparison.Ordinal)).ToString();
            var renderStatements = render.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Phases").Body.Statements;
            string captureClock = string.Join("\n", renderStatements.SkipWhile(s =>
                !s.ToString().StartsWith("LogTiming(\"pool\"", StringComparison.Ordinal)).Skip(1).TakeWhile(s =>
                !s.ToString().StartsWith("LogTiming(\"startmusic-released\"", StringComparison.Ordinal)));
            string code = Fixture
                .Replace("__RECORDING__", recording, StringComparison.Ordinal)
                .Replace("__RETRY_FIELD__", playbackType.Members.OfType<FieldDeclarationSyntax>()
                    .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "keybdStartRetryClock")).ToString(), StringComparison.Ordinal)
                .Replace("__FAIL_MEMBERS__", FailMembers(playbackType), StringComparison.Ordinal)
                .Replace("__RECORDED_ACCURACY__", recordedAccuracy, StringComparison.Ordinal)
                .Replace("__INFO_ACCURACY__", infoAccuracy, StringComparison.Ordinal)
                .Replace("__PUMP__", Source(Method("Features/Replay/Runtime/ReplayPlayer.cs", "ReplayPlayer", "PumpInput", 2)), StringComparison.Ordinal)
                .Replace("__HOTKEY_MEMBERS__", hotkeyMembers, StringComparison.Ordinal)
                .Replace("__BLOCKED__", blocked + Source(Method("State/SpectreState.cs", "SpectreState", "IsBlockedKey")), StringComparison.Ordinal)
                .Replace("__RENDER_WAIT__", Source(Method("Features/VideoRender/Pipeline/VideoRenderJob.cs", "VideoRenderJob", "AwaitReplayAlive")
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space))), StringComparison.Ordinal)
                .Replace("__PREPARE_BRANCH__", prepareBranch, StringComparison.Ordinal)
                .Replace("__SAVE_GLOBAL__", StaticSource(Method("Features/VideoRender/Pipeline/VideoRenderJob.cs", "VideoRenderJob", "SaveGlobalState")), StringComparison.Ordinal)
                .Replace("__RESTORE_GLOBAL__", StaticSource(Method("Features/VideoRender/Pipeline/VideoRenderJob.cs", "VideoRenderJob", "RestoreGlobalState")), StringComparison.Ordinal)
                .Replace("__SAVED_FIELDS__", string.Join("\n", render.Members.OfType<FieldDeclarationSyntax>().Where(f =>
                    f.Declaration.Variables.Any(v => v.Identifier.ValueText.StartsWith("saved", StringComparison.Ordinal)))
                    .Select(f => f.WithModifiers(f.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space)))).Select(f => f.ToString())), StringComparison.Ordinal)
                .Replace("__CAPTURE_CLOCK__", captureClock, StringComparison.Ordinal)
                .Replace("__CLEANUP_PROPERTY__", Type("State/AudioRecorder.cs", "AudioRecorder").Members.OfType<PropertyDeclarationSyntax>()
                    .Single(p => p.Identifier.ValueText == "IsCleanupPending").ToString(), StringComparison.Ordinal);
            string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            if (string.IsNullOrEmpty(trusted)) throw new InvalidOperationException("runtime reference set missing");
            var references = trusted.Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("LifecycleFixture_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(code) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)));
            var fixture = Assembly.Load(image.ToArray()).GetType("LifecycleFixture");
            var lines = (string[])fixture.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            foreach (string line in lines) Console.WriteLine("  " + line);
            int failures = lines.Count(line => line.StartsWith("✗", StringComparison.Ordinal));

            return failures;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ 生命周期真实源码回归无法执行：" + (ex.InnerException ?? ex).Message);
            return 1;
        }
    }

    private static ClassDeclarationSyntax Type(string path, string name)
        => CSharpSyntaxTree.ParseText(SrcUtil.Source(Driver.ProjectDir, path)).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == name);

    private static MethodDeclarationSyntax Method(string path, string type, string name, int? parameterCount = null)
        => Type(path, type).Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name
            && (!parameterCount.HasValue || m.ParameterList.Parameters.Count == parameterCount.Value));

    private static string Source(MethodDeclarationSyntax method)
        => method.WithAttributeLists(default).ToString();

    private static string FailMembers(ClassDeclarationSyntax playback)
        => string.Join("\n", playback.Members.OfType<FieldDeclarationSyntax>()
               .Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "keybdStartFailClock" || v.Identifier.ValueText == "keybdStartFailNotified"))
               .Select(f => f.ToString()))
           + "\n" + Source(Method("Features/Replay/Patches/Patch_Playback.cs", "Patch_PlayerControlUpdate", "NoteKeybdStartFailure"));

    private static string StaticSource(MethodDeclarationSyntax method)
        => Source(method.AddModifiers(SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space)));

    private const string Fixture = """
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Settings = SpectreReplay.Settings;

public static class LifecycleFixture
{
    static readonly List<string> results = new List<string>();
    static bool is_recording = true, is_playing = true;
    static object __instance = new object();
    static ReplayData data = new ReplayData();
    static int KeyEventIndex, recordedFrames;
    static bool _pendingFirstKeyLog, _loggedMirrorFallback, _mirrorUsedThisPlayback;
    static byte[] CurrKeyState = new byte[256];
    static readonly List<KeyboardSimulation.INPUT> _inputBuffer = new List<KeyboardSimulation.INPUT>();
    internal static double BaseAccuracy = 1.0, CrossAccuracy = 0.8;
    static double ComputePercentXacc() => BaseAccuracy;
    static void Record_kbdstate() { recordedFrames++; }
    static bool IsExtendedKey(ushort key) => false;
    __RETRY_FIELD__
    __FAIL_MEMBERS__
    __BLOCKED__
    __PUMP__
    __RENDER_WAIT__
    __SAVE_GLOBAL__
    __RESTORE_GLOBAL__
    __SAVED_FIELDS__
    static void BeginCaptureClock(int fps) { __CAPTURE_CLOCK__ }
    static int requestedStartFloor;
    static void PrepareLevel() { __PREPARE_BRANCH__ }
    static bool playbackLive, cancelRequested, sawPositivePosition;
    static string playbackRejectReason;
    static System.Diagnostics.Stopwatch _jobClock = System.Diagnostics.Stopwatch.StartNew();
    static void LogTiming(string stage, string detail = null) { }
    static void Frame() { __RECORDING__ }
    static readonly List<string> Warnings = new List<string>();
    static void TriggerMessage(string message, float extra = 0f, NotifType type = NotifType.Info)
    {
        if (type == NotifType.Warning) Warnings.Add(message);
    }
    static void StartStreak(AudioRecorder audio, bool cleanup)
    {
        data = new ReplayData();
        audio.isRecording = false;
        audio.cleanupPending = cleanup;
        audio.NextSucceeds = false;
    }
    static void EndStreak(AudioRecorder audio)
    {
        audio.cleanupPending = false;
        audio.NextSucceeds = true;
        Frame();
        Frame();
        data = new ReplayData();
    }
    static void SetFailClock(long ms) { if (keybdStartFailClock != null) keybdStartFailClock.ElapsedMilliseconds = ms; }
    static void SetRetryClock(long ms) { if (keybdStartRetryClock != null) keybdStartRetryClock.ElapsedMilliseconds = ms; }
    static void KeybdStartFailure()
    {
        var audio = AudioRecorder.Instance;
        Warnings.Clear();
        keybdStartFailClock = null;
        keybdStartFailNotified = false;
        // A：录音线程卡住（清理未完）。起录不被调用，但失败仍要计时。
        int a0 = audio.Attempts;
        StartStreak(audio, cleanup: true);
        Frame();
        bool quiet = Warnings.Count == 0 && keybdStartFailClock != null && audio.Attempts == a0;
        SetFailClock(1999);
        Frame();
        quiet &= Warnings.Count == 0;
        SetFailClock(2000);
        Frame();
        bool shown = Warnings.Count == 1 && Warnings[0] == "note.keybd_start_fail";
        Frame();
        Frame();
        bool once = Warnings.Count == 1;
        EndStreak(audio);
        bool cleared = keybdStartFailClock == null && !keybdStartFailNotified;
        // B：StartRecording 返回 false，退避重试期间同样计时，同一段只提示一次。
        int b0 = audio.Attempts;
        int warned = Warnings.Count;
        StartStreak(audio, cleanup: false);
        Frame();
        bool viaStart = audio.Attempts == b0 + 1 && keybdStartRetryClock != null && Warnings.Count == warned;
        SetFailClock(2000);
        SetRetryClock(500);
        Frame();
        viaStart &= audio.Attempts == b0 + 2 && Warnings.Count == warned + 1;
        SetRetryClock(500);
        Frame();
        viaStart &= Warnings.Count == warned + 1;
        EndStreak(audio);
        Check(quiet && shown && once && cleared && viaStart,
            "LC23 起录持续失败满 2 秒才提示一次（线程卡住与 StartRecording 失败同计），同段只提示一次，成功后清零");
    }
    static void RecordAccuracy() { __RECORDED_ACCURACY__ }
    static Info ReadAccuracy()
    {
        var info = new Info { PercentXacc = data.PercentXacc ?? 0.0 };
        __INFO_ACCURACY__
        return info;
    }
    static void Check(bool condition, string label) => results.Add((condition ? "✓ " : "✗ ") + label);
    static bool Near(double a, double b) => Math.Abs(a - b) < 0.0000001;

    public static string[] Run()
    {
        Recording();
        KeybdStartFailure();
        Accuracy();
        Mirroring();
        RenderManualStart();
        RenderClockLifecycle();
        return results.ToArray();
    }

    static void Recording()
    {
        data = new ReplayData();
        var audio = AudioRecorder.Instance;
        audio.cleanupPending = true;
        File.Deletes = 0;
        Frame();
        Check(audio.Attempts == 0 && File.Deletes == 0 && !data.KeybdSoundStartTick.HasValue && recordedFrames == 1,
            "LC02 旧录音清理期间不删临时音频、不起录，按键仍记录");
        audio.cleanupPending = false;
        audio.NextSucceeds = false;
        Frame();
        bool retry = audio.Attempts == 1 && !data.KeybdSoundStartTick.HasValue && recordedFrames == 2;
        int deleted = File.Deletes;
        Frame();
        Check(audio.Attempts == 1 && File.Deletes == deleted && recordedFrames == 3,
            "LC11 起录失败退避期间不重复删文件或起录，按键仍记录");
        audio.NextSucceeds = true;
        if (keybdStartRetryClock != null) keybdStartRetryClock.ElapsedMilliseconds = 500;
        Frame();
        retry &= audio.Attempts == 2 && data.KeybdSoundStartTick is double tick && double.IsNaN(tick) && recordedFrames == 4;
        ADOBase.conductor = new scrConductor { songposition_minusi = 1.25 };
        Frame();
        Frame();
        retry &= audio.Attempts == 2 && data.KeybdSoundStartTick == 1.25 && recordedFrames == 6;
        Check(retry, "LC01 起录返回false后重试，成功才占NaN且导体就绪补tick");
    }


    static void Accuracy()
    {
        bool recording = true, native = true, cross = true;
        foreach (string scene in new[] { "scnEditor", "scnGame", null })
        foreach (int used in new[] { 0, 1, 3 })
        {
            data = new ReplayData { SceneName = scene };
            scrController.checkpointsUsed = used;
            BaseAccuracy = 1.0;
            RecordAccuracy();
            double factor = scene == "scnEditor" ? 1.0 : Math.Pow(0.9875, used);
            recording &= data.CheckpointsUsed == used && Near(data.PercentXacc.Value, factor);
            Info summary = ReadAccuracy();
            native &= Near(summary.Xacc, factor);
            cross &= Near(summary.CrossXacc, 0.8 * factor);
        }
        data = new ReplayData { SceneName = "scnGame", CheckpointsUsed = null };
        Info legacy = ReadAccuracy();
        native &= Near(legacy.Xacc, 1.0);
        cross &= Near(legacy.CrossXacc, 0.8);
        Check(recording, "LC06 录制按来源场景豁免编辑器XAcc，仍保留真实检查点次数");
        Check(native, "LC07 信息页当前XAcc按回放来源豁免，未知次数不伪造");
        Check(cross, "LC08 信息页跨口径XAcc使用相同编辑器豁免");
    }

    static bool Pump(ushort key, bool asyncInput, bool mirror, ushort mapped = 0)
    {
        is_playing = true;
        data = new ReplayData();
        data.KeyEvents.Add(new KeyEvent { KeyCode = key, IsPressed = true, SongPosition = 1 });
        KeyEventIndex = 0;
        _pendingFirstKeyLog = false;
        _loggedMirrorFallback = false;
        _mirrorUsedThisPlayback = false;
        CurrKeyState = new byte[256];
        for (int i = 0; i < 256; i++) Settings.key_code_convert[i] = (ushort)i;
        if (mapped != 0) Settings.key_code_convert[key] = mapped;
        ManagedInput.Noted.Clear();
        AsyncInputInjector.Injected.Clear();
        AsyncInputInjector.Injectable = asyncInput;
        ManagedInput.Mirror = mirror;
        KeyboardSimulation.Sent.Clear();
        PumpInput(1.0, new scrConductor());
        return _mirrorUsedThisPlayback;
    }

    static void Mirroring()
    {
        bool isolated = true;
        foreach (bool asyncInput in new[] { true, false })
        foreach (ushort key in new ushort[] { 120, 121, 122 })
        {
            isolated &= !Pump(key, asyncInput, true) && ManagedInput.Noted.Count == 0
                && AsyncInputInjector.Injected.Count == 0 && KeyboardSimulation.Sent.Count == 0;
        }
        isolated &= !Pump(65, true, true, 122) && ManagedInput.Noted.Count == 0 && KeyboardSimulation.Sent.Count == 0;
        bool ordinary = Pump(65, true, true) && ManagedInput.Noted.Count == 1
            && AsyncInputInjector.Injected.Count == 1 && KeyboardSimulation.Sent.Count == 1;
        ordinary &= !Pump(65, true, false) && ManagedInput.Noted.Count == 1 && KeyboardSimulation.Sent.Count == 0;
        ordinary &= !Pump(16, true, true) && ManagedInput.Noted.Count == 1 && KeyboardSimulation.Sent.Count == 0;
        Check(isolated && ordinary, "LC09 同步/异步镜像过滤F9/F10/F11及映射后热键，普通键/修饰键规则保留");
    }

    static IEnumerator RenderWait(States state = States.Start, bool playing = false, bool pendingMusic = false)
    {
        playbackLive = cancelRequested = sawPositivePosition = false;
        playbackRejectReason = null;
        scrController.instance = new scrController { state = state };
        ADOBase.conductor = new scrConductor();
        ADOBase.isScnGame = ADOBase.editor == null;
        ADOBase.customLevel = new scnGame();
        SpectreState.is_playing = playing;
        Patch_VideoDeferStartMusic.Pending = pendingMusic;
        Patch_StartRewind.LastReject = null;
        return AwaitReplayAlive();
    }

    static void RenderManualStart()
    {
        ADOBase.editor = null;
        requestedStartFloor = 0;
        var waiting = RenderWait();
        var controller = scrController.instance;
        scrController.Restarts = 0;
        PrepareLevel();
        bool waitsForKey = waiting.MoveNext() && !playbackLive;
        Check(scrController.Restarts == 0 && ReferenceEquals(controller, scrController.instance)
            && controller.state == States.Start && !SpectreState.is_playing && !Patch_VideoDeferStartMusic.Pending && waitsForKey,
            "LC12 点击普通关卡渲染只准备等待，不重启、不自行起跑");

        Patch_VideoDeferStartMusic.Pending = true;
        waitsForKey &= waiting.MoveNext() && !playbackLive;
        Patch_VideoDeferStartMusic.Pending = false;
        SpectreState.is_playing = true;
        waitsForKey &= waiting.MoveNext() && !playbackLive;
        Patch_VideoDeferStartMusic.Pending = true;
        waitsForKey &= !waiting.MoveNext() && playbackLive;
        Check(waitsForKey, "LC13 等待玩家起跑，回放接管且歌曲排程就绪后才开始捕获");

        ADOBase.editor = new scnEditor();
        var editor = RenderWait(States.Countdown, playing: true, pendingMusic: true);
        PrepareLevel();
        Check(ADOBase.editor.PlayCalls == 1 && !editor.MoveNext() && playbackLive,
            "LC14 编辑器仍调用原地Play并直接接管倒计时");
        ADOBase.editor = null;
        var canceled = RenderWait();
        bool canCancel = canceled.MoveNext();
        cancelRequested = true;
        Check(canCancel && !canceled.MoveNext() && !playbackLive, "LC15 等待手动起跑期间取消可立即退出");
    }

    static void RenderClockLifecycle()
    {
        ADOBase.editor = null;
        Time.RealTime = 5;
        Time.captureFramerate = 0;
        Application.targetFrameRate = 144;
        QualitySettings.vSyncCount = 1;
        Application.runInBackground = false;
        AudioListener.pause = true;
        GCS.checkpointNum = 3;
        UnscaledDeltaPin.Uninstall();
        SaveGlobalState(60);
        double transitionStart = Time.unscaledTime;
        Time.RealTime += 0.5;
        Check(!UnscaledDeltaPin.Installed && Time.unscaledTime - transitionStart >= 0.3 && Time.captureFramerate == 0,
            "LC18 普通关卡等待起跑时真实时钟继续推进，预备状态不被冻结");
        BeginCaptureClock(60);
        Check(Patch_VideoDeferStartMusic.ReleasedWithClock && UnscaledDeltaPin.Installed,
            "LC19 回放接管且捕获就绪后先装渲染时钟，再释放歌曲排程");
        RestoreGlobalState();
        double afterRelease = Time.unscaledTime;
        Time.RealTime += 0.5;
        Check(!UnscaledDeltaPin.Installed && !UnscaledDeltaPin.DspInstalled && Time.unscaledTime > afterRelease
            && Time.captureFramerate == 0 && Application.targetFrameRate == 144 && QualitySettings.vSyncCount == 1
            && !Application.runInBackground && AudioListener.pause && GCS.checkpointNum == 3,
            "LC20 收尾还原真实时钟、帧率和全局状态");
        SaveGlobalState(60);
        RestoreGlobalState();
        Check(!UnscaledDeltaPin.Installed && !UnscaledDeltaPin.DspInstalled && Time.captureFramerate == 0
            && Application.targetFrameRate == 144 && QualitySettings.vSyncCount == 1
            && !Application.runInBackground && AudioListener.pause && GCS.checkpointNum == 3,
            "LC21 场景等待期间取消或超时也完整恢复，未捕获时不遗留渲染时钟");
        ADOBase.editor = new scnEditor();
        SaveGlobalState(60);
        Check(UnscaledDeltaPin.Installed, "LC22 编辑器保持原有准备期时钟装配");
        RestoreGlobalState();
        ADOBase.editor = null;
    }
}

class ReplayData { public List<KeyEvent> KeyEvents = new List<KeyEvent>(); public double? KeybdSoundStartTick; public string SceneName; public int? CheckpointsUsed; public double? PercentXacc; }
class KeyEvent { public ushort KeyCode; public bool IsPressed; public double SongPosition; }
class Info { public int[] JudgmentCounts = new int[1]; public double PercentXacc, Xacc, CrossXacc, CrossAccuracy; }
class scrConductor { public object song = new object(); public double songposition_minusi; public bool hasSongStarted; public int countdownTicks = 4; public float countdownSpeedMultiplier = 1; public double crotchetAtStart = 0.5; }
namespace System.Diagnostics { class Stopwatch { public long ElapsedMilliseconds; public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMilliseconds); public static Stopwatch StartNew() => new Stopwatch(); } }
static class ADOBase { public static scrConductor conductor; public static bool isScnGame; public static scnGame customLevel; public static LevelMaker lm; public static scnEditor editor; public static bool isLevelEditor => editor != null; }
class LevelMaker { public List<object> listFloors; }
class scnEditor { public int PlayCalls; public List<object> floors = new List<object> { new object(), new object() }; public void SelectFloor(object floor, bool cameraJump) { } public void Play() { PlayCalls++; } }
static class Application { public static bool isFocused = true, runInBackground; public static int targetFrameRate; }
static class QualitySettings { public static int vSyncCount; }
static class Time { public static int captureFramerate; public static double RealTime; public static double unscaledTime => UnscaledDeltaPin.Installed ? UnscaledDeltaPin.Base : RealTime; }
static class UnscaledDeltaPin { public static bool Installed, DspInstalled; public static double Base; public static void Install(int fps) { Base = Time.RealTime; Installed = true; } public static void InstallDsp() { DspInstalled = true; } public static void Uninstall() { Installed = DspInstalled = false; } }
static class File { public static int Deletes; public static bool Exists(string path) => true; public static void Delete(string path) { Deletes++; } }
class AudioRecorder
{
    public static AudioRecorder Instance = new AudioRecorder();
    public bool cleanupPending, isRecording, NextSucceeds;
    public int Attempts;
    public bool IsRecording => isRecording;
    __CLEANUP_PROPERTY__
    public bool StartRecording(string path, string device) { Attempts++; isRecording = NextSucceeds; return NextSucceeds; }
}
enum NotifType { Info, Warning }
enum KeyCode { F9 = 290, F10 = 291, F11 = 292 }
namespace SpectreReplay { static class Settings { public static bool KeybdSoundRecordActive = true; public static string SavePath = "memory-only", MicrophoneDeviceName = "fixture"; public static int SaveButton = 290, CancelSaveButton = 291, RenderStopKey = 292; public static ushort[] key_code_convert = new ushort[256]; } }
static class Debug { public static void Log(string value) { } public static void LogWarning(string value) { } public static void LogException(Exception value) { } }
static class ManagedInput
{
    public static bool Mirror;
    public static List<ushort> Noted = new List<ushort>();
    public static bool Note(ushort key, bool pressed, ushort source, out bool aggregateChanged, out ushort aggregateVk)
    { Noted.Add(key); aggregateChanged = true; aggregateVk = key; return true; }
    __HOTKEY_MEMBERS__
}
static class KeyViewerBridge { public static int CachedNativeVk(KeyCode key) => (int)key - 170; }
static class AsyncInputInjector { public static bool Injectable, Healthy = true; public static List<ushort> Injected = new List<ushort>(); public static void Inject(ushort key, bool pressed, double time) { Injected.Add(key); } }
class Patch_MirrorEchoSuppressor { }
namespace PatchShared { static class SpectrePatchHub { public static bool IsApplied(Type type) => true; } }
static class KeyboardSimulation { public struct INPUT { public ushort Key; } public static List<ushort> Sent = new List<ushort>(); public static INPUT MakeInput(byte key, bool pressed, bool extended) => new INPUT { Key = key }; public static void SendInputs(List<INPUT> inputs) { foreach (var input in inputs) Sent.Add(input.Key); } }
namespace Features.VideoRender { static class RenderOverlayCapture { public static void QuartzKey(ushort key, bool pressed) { } } }
static class QuartzLiveBridge { public static void Edge(ushort key, bool pressed) { } }
static class ReplayInputBridge { public static void Edge(ushort key, bool pressed) { } }
namespace SpectreReplay.Features.Compat { static class HitMarginCompat { public static bool HasXPerfect = true; public static double XAcc(int[] counts) => LifecycleFixture.BaseAccuracy; public static double LegacyXAcc(int[] counts) => LifecycleFixture.CrossAccuracy; public static double LegacyAccuracy(int[] counts) => 1.0; } }
class scrController { public static int checkpointsUsed, Restarts; public static scrController instance; public States state; public void Restart() { Restarts++; } }
class scnGame { public bool isLoading; }
enum States { Start, Countdown, PlayerControl, Checkpoint, Fail2 }
static class AudioListener { public static bool pause; }
static class Mathf { public static int Max(int a, int b) => Math.Max(a, b); public static float Max(float a, float b) => Math.Max(a, b); public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max); }
static class Patch_VideoDeferStartMusic { public static bool Pending, ReleasedWithClock; public static void Release() { ReleasedWithClock = UnscaledDeltaPin.Installed && UnscaledDeltaPin.DspInstalled && Time.captureFramerate == 60; } public static void ReleaseForTeardown() { } }
static class Patch_StartRewind { public static string LastReject; }
static class LocalizationManager { public static string GetLocalizedText(string key) => key; }
static class SpectreAPI { public static Dictionary<string, string> SnapshotBlocks() => new Dictionary<string, string>(); }
static class GCS { public static int checkpointNum; }
static class SpectreState { public static ReplayData data; public static bool is_playing, PlayMode, RecordMode; }
""";
}
