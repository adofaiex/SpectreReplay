using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// 执行当前产品方法。屏障只安排合法线程交错；不复制池、时钟、封头或保存算法。
internal static class BoundaryRegressionCheck
{
    internal static int Run() => Execute(Driver.ProjectDir, "Run");
    internal static int RunNotifications(string projectRoot) => Execute(projectRoot, "RunNotifications");

    private static int Execute(string projectRoot, string entry)
    {
        try
        {
            var audio = Type(projectRoot, "State/AudioRecorder.cs", "AudioRecorder");
            var state = Type(projectRoot, "State/SpectreState.cs", "SpectreState");
            var misc = Type(projectRoot, "Features/Replay/Patches/Patch_Misc.cs", "Patch_ConductorUpdate");
            var stopCaller = Method(Type(projectRoot, "Features/Replay/Runtime/ReplayRecorder.cs", "ReplayRecorder"), "Stop")
                .Body.Statements.OfType<IfStatementSyntax>().Where(s => s.Condition.ToString() == "AudioRecorder.Instance.IsRecording"
                    || s.Condition.ToString().StartsWith("Settings.KeybdSoundRecordActive", StringComparison.Ordinal));
            string code = Fixture
                .Replace("__STOP_CALLER__", string.Join("\n", stopCaller), StringComparison.Ordinal)
                .Replace("__AUDIO__", Members(audio, "hashAlgorithm", "hashLock", "isHashFinalized", "isRecording", "recordingClip",
                    "microphoneDevice", "processingThread", "initThread", "finalizeThread", "finalizedHash", "cleanupPending", "WorkersRunning",
                    "bufferLock", "wavDataBuffer", "fileStream", "currentFilePath", "LastStop", "AudioHash", "IsRecording", "IsCleanupPending", "WavHeaderSize")
                    + Methods(audio, "StopRecording", "FinalizeRecording", "FinalizeWavFile", "CloseFileStreamQuietly", "Cleanup", "CreateWAVHeader", "TryComputePcmHash", "VerifyAudioFile")
                    + audio.Members.OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "PendingCleanup").ToString()
                        .Replace("private void Update()", "internal void UpdateForTest()", StringComparison.Ordinal), StringComparison.Ordinal)
                .Replace("__SAVE__", Methods(state, "SaveFile", "CompleteSave") + "\n"
                    + state.Members.OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "SaveCompletion"), StringComparison.Ordinal)
                .Replace("__HOTKEY__", Type(projectRoot, "Features/Replay/Patches/Patch_Misc.cs", "ManualSaveHotkey").ToString(), StringComparison.Ordinal)
                .Replace("__MANUAL__", Members(misc, "StateFail", "StateFail2", "StateWon") + Methods(misc, "Postfix"), StringComparison.Ordinal);
            string[] pure = { "State/RecordingStop.cs", "Features/Replay/Data/ReplayData.cs",
                "Features/Replay/Data/Replay.cs", "Features/Replay/Data/ReplayConstants.cs",
                "Features/Replay/Data/keyEvent.cs", "Features/Replay/Data/HitSnapshot.cs", "Features/Replay/Data/CursorSample.cs",
                "Features/Replay/Data/KeybdAudioBlob.cs", "Features/Replay/Data/KeybdPrefs.cs",
                "Features/Replay/Codec/ReplayEncoder.cs", "Features/Replay/Codec/ReplayDecoder.cs",
                "Features/Replay/Codec/ReplayCompression.cs", "Features/Replay/Codec/ReplayCrypto.cs",
                "Features/Replay/Codec/KeybdAudioCodec.cs", "Features/Replay/Runtime/ReplaySaveOperation.cs" };
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var trees = new[] { code }.Concat(pure.Select(p => SrcUtil.Source(projectRoot, p))).Select(s => CSharpSyntaxTree.ParseText(s));
            var compilation = CSharpCompilation.Create("BoundaryFixture_" + Guid.NewGuid().ToString("N"), trees, trusted,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Take(12)));
            var fixture = Assembly.Load(image.ToArray()).GetType("BoundaryFixture");
            var results = (string[])fixture.GetMethod(entry).Invoke(null, null);
            foreach (string result in results) Console.WriteLine("  " + result);
            return results.Count(r => r.StartsWith("✗", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ " + entry + " 边界真实源码回归无法执行：" + (ex.InnerException ?? ex).Message);
            return 1;
        }
    }

    private static ClassDeclarationSyntax Type(string root, string path, string name)
        => CSharpSyntaxTree.ParseText(SrcUtil.Source(root, path)).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == name);
    private static MethodDeclarationSyntax Method(ClassDeclarationSyntax type, string name)
        => type.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
    private static string Methods(ClassDeclarationSyntax type, params string[] names)
        => string.Join("\n", type.Members.OfType<MethodDeclarationSyntax>().Where(m => names.Contains(m.Identifier.ValueText))
            .Select(m => m.WithAttributeLists(default).ToString()));
    private static string Members(ClassDeclarationSyntax type, params string[] names)
        => string.Join("\n", type.Members.Where(m => m is FieldDeclarationSyntax f
            && f.Declaration.Variables.Any(v => names.Contains(v.Identifier.ValueText))
            || m is PropertyDeclarationSyntax p && names.Contains(p.Identifier.ValueText)));

    private const string Fixture = """
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using SpectreReplay;
using SpectreReplay.Features.Replay;
using static SpectreState;
using ThreadPriority = System.Threading.ThreadPriority;

public static class BoundaryFixture
{
    static readonly List<string> results = new List<string>();
    internal static readonly List<string> Saved = new List<string>();
    internal static readonly List<int> EventThreads = new List<int>();
    internal static readonly int MainId = Thread.CurrentThread.ManagedThreadId;
    internal static bool Reenter;
    static void Check(bool ok, string label) => results.Add((ok ? "✓ " : "✗ ") + label);
    static void Drain(ReplaySaveOperation operation)
    {
        if (operation == null || !SpinWait.SpinUntil(() => operation.IsCompleted, 10000))
            throw new TimeoutException("save worker did not complete");
        GameObject.TickAll(); GameObject.TickAll();
    }
    static void ResetProbe()
    {
        SaveProbe.Reset(); Saved.Clear(); EventThreads.Clear(); SpectreState.Messages.Clear();
    }
    public static string[] Run()
    {
        Audio();
        return results.ToArray();
    }
    static void Audio()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SpectreReplay-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var audio = new AudioRecorder();
        AudioRecorder.Current = audio;
        try
        {
            audio.Prepare(directory, false);
            SpectreState.data = NewData(null);
            bool bounded = audio.StopBounded();
            var completion = audio.LastStop;
            bool callerLinked = ReferenceEquals(SpectreState.data.KeybdSoundStop, completion);
            bool waits = audio.IsCleanupPending && !completion.IsCompleted;
            Settings.SavePath = directory;
            ResetProbe();
            bool accepted = SpectreState.SaveFile();
            var operation = SpectreState.data.SaveOperation;
            SpectreState.SaveFile();
            // 别名对象也带着同一录音凭据；不能只靠原对象的 SaveOperation 去重。
            SpectreState.SaveFile(savedData: SpectreState.data.SnapshotForSave());
            int prematureSaves = SaveProbe.Attempts;
            SpectreState.data.KeyEvents[0].KeyCode = 99;
            SpectreState.data.HitSnapshots[0].CurrentFloorID = 99;
            SpectreState.data.CursorTrack[0].X = 0.9f;
            SpectreState.data.Reset();
            Settings.SavePath = Path.Combine(directory, "next-run");
            SpectreState.data.FileName = "next-run";
            audio.Release.Set();
            audio.WaitFinished();
            byte[] sealedWav = completion.AudioPath == null ? Array.Empty<byte>() : File.ReadAllBytes(completion.AudioPath);
            // 封头已转交独占文件，但尚未发布完成；下一局不能认领它。
            ReplayStopCaller.Stop();
            Settings.SavePath = directory;
            SpectreState.SaveFile();
            Drain(SpectreState.data.SaveOperation);
            bool nextRunKeptAudio = string.IsNullOrEmpty(SaveProbe.Last.Metadata.KeybdSoundFileName)
                && completion.AudioPath != null && File.Exists(completion.AudioPath);
            ResetProbe();
            Settings.SavePath = Path.Combine(directory, "next-run");
            audio.PumpCleanup();
            Drain(operation);
            var saved = SaveProbe.Last;
            string hash = sealedWav.Length > 44 ? Convert.ToHexString(SHA256.HashData(sealedWav.Skip(44).ToArray())).ToLowerInvariant() : null;
            Check(bounded && waits && callerLinked, "BD07 写盘持锁时停止限时返回，调用方保留音频完成凭据");
            Check(audio.WorkerHealthy && sealedWav.Length == 50 && BitConverter.ToInt32(sealedWav,4) == 42
                && BitConverter.ToInt32(sealedWav,40) == 6 && sealedWav.Skip(44).SequenceEqual(new byte[] {1,2,3,4,9,10})
                && completion.IsCompleted && completion.Hash == hash && saved.Metadata.KeybdSoundHash == hash && saved.KeybdAudio.HasData,
                "BD08 后台封头包含初始化积压尾块，RIFF长度与PCM哈希匹配");
            Check(accepted && prematureSaves == 0 && SaveProbe.Attempts == 1 && Saved.Count == 1 && SaveProbe.GateHeldAtQueue
                && operation.WorkerThreadId != MainId && EventThreads.All(t => t == MainId),
                "BD09 音频完整前不保存，重复请求去重，清理门内排队且后台保存/主线程发布");
            Check(saved.Metadata.FileName == "first" && Path.GetDirectoryName(SaveProbe.LastPath) == directory
                && saved.KeyEvents.Count == 1 && saved.KeyEvents[0].KeyCode == 65
                && saved.HitSnapshots.Count == 1 && saved.HitSnapshots[0].CurrentFloorID == 1
                && saved.CursorTrack.Count == 1 && saved.CursorTrack[0].X == 0.5f
                && SpectreState.data.FileName == "next-run" && SpectreState.data.KeybdSoundStop == null,
                "BD10 延迟保存保留三轨独立列表/元素和原目录，Reset不串局");
            Check(nextRunKeptAudio, "BD14 下一局尚未起录时不认领上一局待保存的独占WAV");

            var retryStop = new RecordingStop();
            SpectreState.data = NewData(retryStop);
            SpectreState.data.KeybdSoundFileName = null;
            Settings.SavePath = directory;
            ResetProbe(); SaveProbe.FailuresRemaining = 1;
            SpectreState.SaveFile(notify: false);
            var retry = SpectreState.data.SaveOperation;
            SpectreState.data = new ReplayData { FileName = "unrelated" };
            Settings.SavePath = Path.Combine(directory, "next-run");
            retryStop.Complete("hash");
            Drain(retry);
            Check(SaveProbe.Attempts == 2 && SaveProbe.Last.KeyEvents.Count == 1 && SaveProbe.Last.Metadata.FileName == "safe"
                && Path.GetDirectoryName(SaveProbe.LastPath) == directory && SpectreState.Messages.Count == 0,
                "BD13 延迟保存失败重试仍使用原快照/目录，并保留调用方的通知选择");

            audio = new AudioRecorder();
            AudioRecorder.Current = audio;
            audio.Prepare(directory, true);
            SpectreState.data = NewData(null);
            bool sealBounded = audio.StopBounded();
            bool sealPending = audio.IsCleanupPending && !audio.LastStop.IsCompleted;
            audio.Release.Set(); audio.WaitFinished(); audio.PumpCleanup();
            Check(sealBounded && sealPending && audio.FinalizeOnWorker,
                "BD11 慢Flush也由后台线程承担，完成前不回收或在主线程补等锁");

            var failedStop = new RecordingStop();
            failedStop.Complete(null);
            SpectreState.data = NewData(failedStop);
            Settings.SavePath = directory;
            ResetProbe();
            SpectreState.SaveFile();
            Drain(SpectreState.data.SaveOperation);
            Check(string.IsNullOrEmpty(SaveProbe.Last.Metadata.KeybdSoundFileName)
                && SpectreState.Messages.Any(m => m.Contains("note.keybdsoundfile_save_fail")),
                "BD12 音频封头失败时显式提示，不保存未完成WAV引用");
        }
        finally
        {
            audio.Release.Set(); audio.WaitFinished(); audio.PumpCleanup();
            Directory.Delete(directory, true);
        }
    }
    public static string[] RunNotifications()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SpectreReplay-toast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); Settings.SavePath = directory;
        try
        {
            ResetProbe(); SaveProbe.FailuresRemaining = 1;
            SaveProbe.Hold = new ManualResetEventSlim(); SaveProbe.Entered.Reset();
            SpectreState.data = NewData(null); SpectreState.data.KeybdSoundFileName = null;
            bool accepted = SpectreState.SaveFile(); var operation = SpectreState.data.SaveOperation;
            if (!SaveProbe.Entered.Wait(5000)) throw new TimeoutException("save did not enter IO seam");
            GameObject.TickAll(); bool silentWhilePending = SpectreState.Messages.Count == 0;
            SaveProbe.Hold.Set(); Reenter = true; Drain(operation);
            Check(accepted && silentWhilePending && SpectreState.Messages.Count == 1
                && SpectreState.Messages[0].Contains("note.save_success") && SaveProbe.Attempts == 2 && Saved.Count == 1,
                "ST01 默认notify只在安全名称重试成功后发一次最终结果，重入/后续帧不重复");
            Check(operation.WorkerThreadId != MainId && EventThreads.All(t => t == MainId)
                && SpectreState.MessageThreads.All(t => t == MainId), "ST02 保存结果与通知只由主线程完成出口发布");

            ResetProbe(); SaveProbe.FailuresRemaining = 2;
            SpectreState.data = NewData(null); SpectreState.data.KeybdSoundFileName = null;
            SpectreState.SaveFile(notify: true); Drain(SpectreState.data.SaveOperation);
            Check(SaveProbe.Attempts == 2 && Saved.Count == 0 && SpectreState.Messages.Count == 1
                && SpectreState.Messages[0].Contains("note.save_fail"), "ST03 两次写盘均失败只通知一次最终失败");

            ResetProbe(); SaveProbe.FailuresRemaining = 1;
            var stop = new RecordingStop(); SpectreState.data = NewData(stop); SpectreState.data.KeybdSoundFileName = null;
            SpectreState.SaveFile(notify: false); operation = SpectreState.data.SaveOperation;
            SpectreState.data.Reset(); stop.Complete("hash"); Drain(operation);
            Check(SaveProbe.Attempts == 2 && Saved.Count == 1 && SpectreState.Messages.Count == 0,
                "ST04 调用方notify=false穿过音频等待/Reset/安全重试和完成观察器，不产生通知");

            ResetProbe(); ManualSaveHotkey.Reset(); UnityEngine.Time.frameCount = 10;
            SpectreState.PendingSave = true; SpectreState.LateSaveMode = true;
            SpectreState.data = NewData(null); SpectreState.data.KeybdSoundFileName = null;
            Patch_ConductorUpdate.Postfix(); operation = SpectreState.data.SaveOperation; Drain(operation);
            SpectreState.PendingSave = true; SpectreState.data = NewData(null); SpectreState.data.KeybdSoundFileName = null;
            Patch_ConductorUpdate.Postfix();
            Check(SpectreState.StopCalls == 1 && !SpectreState.LateAtStop && SpectreState.data.SaveOperation == null
                && Saved.Count == 1 && SpectreState.Messages.Count == 1,
                "ST05 真实手动保存调用方同帧只消费一次，并先清LateSave防双存");
            UnityEngine.Time.frameCount++; Patch_ConductorUpdate.Postfix(); Drain(SpectreState.data.SaveOperation);
            Check(SpectreState.StopCalls == 2 && Saved.Count == 2 && SpectreState.Messages.Count == 2,
                "ST06 热键守卫仅限一帧，下一帧仍可保存/通知");
            return results.ToArray();
        }
        finally { SaveProbe.Hold?.Set(); Directory.Delete(directory, true); }
    }
    static ReplayData NewData(RecordingStop stop)
    {
        var data = new ReplayData { FileName = "first", KeybdSoundFileName = "first.wav", KeybdSoundStop = stop, KeybdSoundStartTick = 0 };
        data.KeyEvents.Add(new KeyEvent { KeyCode = 65 }); data.HitSnapshots.Add(new HitSnapshot { CurrentFloorID = 1 });
        data.CursorTrack.Add(new CursorSample { X = 0.5f }); return data;
    }
}

class MonoBehaviour { public object gameObject; }
class AudioClip { }
static class Object
{
    public static void Destroy(object value) { GameObject.Remove(value); }
    public static void DontDestroyOnLoad(object value) { }
}
class GameObject
{
    static readonly List<MonoBehaviour> components = new List<MonoBehaviour>();
    public GameObject(string name) { }
    public T AddComponent<T>() where T : MonoBehaviour
    {
        var component = (T)Activator.CreateInstance(typeof(T), true); component.gameObject = this;
        lock (components) components.Add(component);
        if (typeof(T).Name == "SaveCompletion") SaveProbe.GateHeldAtQueue |= AudioRecorder.Current?.IsCleanupPending == true;
        return component;
    }
    public static void Remove(object go) { lock (components) components.RemoveAll(c => ReferenceEquals(c.gameObject, go)); }
    public static void TickAll()
    {
        MonoBehaviour[] copy; lock (components) copy = components.ToArray();
        foreach (var c in copy) c.GetType().GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(c, null);
    }
}
static class Microphone { public static void End(string device) { } }
static class Debug { public static void Log(object value) { } public static void LogWarning(object value) { } public static void LogError(object value) { } }
class AudioRecorder : MonoBehaviour
{
    __AUDIO__
    public static AudioRecorder Current;
    internal static AudioRecorder Instance => Current;
    public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
    readonly ManualResetEventSlim held = new ManualResetEventSlim();
    int callerThread;
    Exception workerError;
    public bool WorkerHealthy => workerError == null;
    SlowStream stream;
    public bool FinalizeOnWorker => stream.FlushThread != callerThread && stream.FlushThread != 0;
    public void Prepare(string directory, bool slowFlush)
    {
        isRecording = true;
        currentFilePath = Path.Combine(directory, "tmp.wav");
        stream = new SlowStream(currentFilePath, slowFlush ? Release : null);
        fileStream = stream;
        fileStream.Write(CreateWAVHeader(1, 44100, 16, 0), 0, 44);
        hashAlgorithm = SHA256.Create();
        if (!slowFlush)
        {
            wavDataBuffer.AddRange(new byte[] {9,10});
            processingThread = new Thread(() =>
            {
                try
                {
                    lock (bufferLock)
                    {
                        held.Set(); Release.Wait();
                        byte[] pcm = {1,2,3,4};
                        fileStream.Write(pcm, 0, pcm.Length);
                        hashAlgorithm.TransformBlock(pcm, 0, pcm.Length, null, 0);
                    }
                }
                catch (Exception ex) { workerError = ex; } // 产品StreamProcessingLoop也隔离采集异常。
            }) { IsBackground = true };
            processingThread.Start();
            if (!held.Wait(3000)) throw new TimeoutException("audio lock");
        }
    }
    public bool StopBounded()
    {
        var caller = new Thread(() => { callerThread = Thread.CurrentThread.ManagedThreadId; ReplayStopCaller.Stop(); }) { IsBackground = true };
        caller.Start();
        if (caller.Join(1800)) return true;
        Release.Set();
        if (!caller.Join(3000)) throw new TimeoutException("stop did not exit after disk released");
        return false;
    }
    public void WaitFinished()
    {
        if (finalizeThread != null && !finalizeThread.Join(3000)) throw new TimeoutException("finalization");
    }
    public void PumpCleanup() { new PendingCleanup { Owner = this }.UpdateForTest(); }
}

class SlowStream : FileStream
{
    ManualResetEventSlim release;
    public int FlushThread;
    public SlowStream(string path, ManualResetEventSlim signal) : base(path, FileMode.Create, FileAccess.Write, FileShare.Read) { release = signal; }
    public override void Flush() { FlushThread = Thread.CurrentThread.ManagedThreadId; release?.Wait(); base.Flush(); }
}
static class ReplayStopCaller
{
    internal static void Stop() { var data = SpectreState.data; __STOP_CALLER__ }
}
static class Settings { public static string SavePath; public static bool Backup, KeybdSoundCompact; public static bool KeybdSoundRecordActive = true; }
static class SpectreState
{
    public static ReplayData data;
    static bool EmptySaveBlocked, debug_no_encryption;
    public static bool DebugMode, PendingSave, LateSaveMode, LateAtStop;
    public static int save_button, StopCalls;
    public static readonly List<string> Messages = new List<string>();
    public static readonly List<int> MessageThreads = new List<int>();
    static void TriggerMessage(string message) { Messages.Add(message); MessageThreads.Add(Thread.CurrentThread.ManagedThreadId); }
    static void NotifySaveSkipped(string message, string reason) { }
    static string CreateFileName(bool safe_mode = false) => "safe";
    public static class PlayActions { public static void StopRecording() { StopCalls++; LateAtStop = LateSaveMode; } }
    __SAVE__
}
static class LocalizationManager { public static string GetLocalizedText(string key) => key; }
class Patch_Hit { } class Patch_UpdateInputConsumeAsync { } class Patch_PlayerControlUpdate { } class Patch_Won_Update { }
static class SpectrePatchHub { public static void EnsureApplied(string reason, params Type[] types) { } public static void ForceRebuild(Type type) { } }
static class Main { public static Mod mod = new Mod(); public class Mod { public string Path = ""; } }
static class SpectreAPI
{
    public static void RaiseReplaySaved(string path)
    {
        BoundaryFixture.Saved.Add(path); BoundaryFixture.EventThreads.Add(Thread.CurrentThread.ManagedThreadId);
        if (BoundaryFixture.Reenter) { BoundaryFixture.Reenter = false; GameObject.TickAll(); }
    }
}
static class CloudReplay { public static bool OnReplaySaved(string path, string audio) => false; }
static class Ui { public static class Loc { public static string T(string key) => key; } }
// 故障注入止于 IO 边界；快照、重试、音频嵌入、加密、通知均由上面的真实源码完成。
static class SaveProbe
{
    public static int Attempts, FailuresRemaining;
    public static bool GateHeldAtQueue;
    public static string LastPath;
    public static Replay Last;
    public static ManualResetEventSlim Hold;
    public static readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
    public static void Reset()
    {
        Attempts = 0; FailuresRemaining = 0; GateHeldAtQueue = false; Last = null; LastPath = null; Hold = null;
        SpectreState.MessageThreads.Clear();
    }
    public static void Write(string path, byte[] bytes)
    {
        Entered.Set(); Hold?.Wait(); Attempts++;
        if (FailuresRemaining-- > 0) throw new IOException("injected write failure");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes);
        LastPath = path;
        Last = ReplayDecoder.Decode(path.EndsWith(ReplayConstants.Extension, StringComparison.Ordinal) ? ReplayCrypto.Decrypt(bytes) : bytes);
    }
}
namespace SpectreReplay { static class AtomicFile { public static void WriteAllBytes(string path, byte[] bytes) => SaveProbe.Write(path, bytes); } }
namespace UnityEngine
{
    static class Time { public static int frameCount; }
    static class Debug { public static void Log(object value) { } }
    static class Object { public static void Destroy(object value) => global::Object.Destroy(value); }
}
__HOTKEY__
static class Patch_ConductorUpdate { __MANUAL__ }
static class Input { public static bool GetKeyDown(int key) => true; }
enum States { Won = 7 }
class scrController { public static scrController instance = new scrController(); public Machine stateMachine = new Machine(); }
class Machine { public object GetState() => States.Won; }
""";
}
