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
    internal static int Run()
    {
        try
        {
            var audio = Type("State/AudioRecorder.cs", "AudioRecorder");
            var stopCaller = Method(Type("Features/Replay/Runtime/ReplayRecorder.cs", "ReplayRecorder"), "Stop")
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
                .Replace("__SAVE__", Methods(Type("State/SpectreState.cs", "SpectreState"), "SaveFile"), StringComparison.Ordinal)
                .Replace("__DATA__", Type("Features/Replay/Data/ReplayData.cs", "ReplayData").ToString(), StringComparison.Ordinal)
                .Replace("__STOP__", Type("State/RecordingStop.cs", "RecordingStop").ToString(), StringComparison.Ordinal);
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("BoundaryFixture_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(code) }, trusted,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)));
            var fixture = Assembly.Load(image.ToArray()).GetType("BoundaryFixture");
            var results = (string[])fixture.GetMethod("Run").Invoke(null, null);
            foreach (string result in results) Console.WriteLine("  " + result);
            return results.Count(r => r.StartsWith("✗", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ 边界真实源码回归无法执行：" + (ex.InnerException ?? ex).Message);
            return 1;
        }
    }

    private static ClassDeclarationSyntax Type(string path, string name)
        => CSharpSyntaxTree.ParseText(SrcUtil.Source(Driver.ProjectDir, path)).GetRoot().DescendantNodes()
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
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ThreadPriority = System.Threading.ThreadPriority;

public static class BoundaryFixture
{
    static readonly List<string> results = new List<string>();
    static void Check(bool ok, string label) => results.Add((ok ? "✓ " : "✗ ") + label);
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
            ReplayManager.Reset();
            bool accepted = SpectreState.SaveFile();
            SpectreState.SaveFile();
            int prematureSaves = ReplayManager.Saves;
            SpectreState.data.Reset();
            Settings.SavePath = Path.Combine(directory, "next-run");
            SpectreState.data.FileName = "next-run";
            audio.Release.Set();
            audio.WaitFinished();
            // 旧流已关闭、主线程完成通知尚未消费tmp；此时下一局可能先停止/保存。
            ReplayStopCaller.Stop();
            Settings.SavePath = directory;
            SpectreState.SaveFile();
            bool nextRunKeptAudio = ReplayManager.SavedAudioName == null && File.Exists(Path.Combine(directory, "tmp.wav"));
            ReplayManager.Reset();
            Settings.SavePath = Path.Combine(directory, "next-run");
            audio.PumpCleanup();
            Check(bounded && waits && callerLinked, "BD07 写盘持锁时停止限时返回，调用方保留音频完成凭据");
            Check(audio.WorkerHealthy && ReplayManager.ValidWav && ReplayManager.Pcm.SequenceEqual(new byte[] {1,2,3,4,9,10})
                && completion.IsCompleted && completion.Hash == ReplayManager.Hash,
                "BD08 后台封头包含初始化积压尾块，RIFF长度与PCM哈希匹配");
            Check(accepted && prematureSaves == 0 && ReplayManager.Saves == 1 && ReplayManager.GateHeldAtSave,
                "BD09 音频完整前不保存或移走tmp，重复请求去重且保存先于下一次起录");
            Check(ReplayManager.SavedName == "first" && ReplayManager.SavedDirectory == directory
                && ReplayManager.Keys == 1 && ReplayManager.Hits == 1 && ReplayManager.Cursors == 1
                && SpectreState.data.FileName == "next-run" && SpectreState.data.KeybdSoundStop == null,
                "BD10 延迟保存保留三轨独立列表和原目录，Reset不串局");
            Check(nextRunKeptAudio, "BD14 下一局尚未起录时不认领上一局待保存的临时WAV");

            var retryStop = new RecordingStop();
            SpectreState.data = NewData(retryStop);
            SpectreState.data.KeybdSoundFileName = null;
            Settings.SavePath = directory;
            ReplayManager.Reset();
            ReplayManager.FailFirst = true;
            int messages = SpectreState.Messages.Count;
            SpectreState.SaveFile(notify: false);
            SpectreState.data = new ReplayData { FileName = "unrelated" };
            Settings.SavePath = Path.Combine(directory, "next-run");
            retryStop.Complete("hash");
            Check(ReplayManager.Saves == 2 && ReplayManager.Keys == 1 && ReplayManager.SavedName == "safe"
                && ReplayManager.SavedDirectory == directory && SpectreState.Messages.Count == messages,
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
            ReplayManager.Reset();
            SpectreState.SaveFile();
            Check(ReplayManager.SavedAudioName == null && SpectreState.Messages.Any(m => m.Contains("note.keybdsoundfile_save_fail")),
                "BD12 音频封头失败时显式提示，不保存未完成WAV引用");
        }
        finally
        {
            audio.Release.Set(); audio.WaitFinished(); audio.PumpCleanup();
            Directory.Delete(directory, true);
        }
    }
    static ReplayData NewData(RecordingStop stop)
    {
        var data = new ReplayData { FileName = "first", KeybdSoundFileName = "first.wav", KeybdSoundStop = stop, KeybdSoundStartTick = 0 };
        data.KeyEvents.Add(new KeyEvent()); data.HitSnapshots.Add(new HitSnapshot()); data.CursorTrack.Add(new CursorSample());
        return data;
    }
}

class MonoBehaviour { public object gameObject; }
class AudioClip { }
static class Object { public static void Destroy(object value) { } public static void DontDestroyOnLoad(object value) { } }
static class UnityEngine { public static class Object { public static void Destroy(object value) { } } }
class GameObject { public GameObject(string name) { } public T AddComponent<T>() where T : new() => new T(); }
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
static class ReplayStopCaller
{
    internal static void Stop() { var data = SpectreState.data; __STOP_CALLER__ }
}
class SlowStream : FileStream
{
    ManualResetEventSlim release;
    public int FlushThread;
    public SlowStream(string path, ManualResetEventSlim signal) : base(path, FileMode.Create, FileAccess.Write, FileShare.Read) { release = signal; }
    public override void Flush() { FlushThread = Thread.CurrentThread.ManagedThreadId; release?.Wait(); base.Flush(); }
}
__STOP__
__DATA__
public class KeyEvent { }
public class HitSnapshot { }
public class CursorSample { }
public struct KeybdAudioBlob { public bool HasData; }
public struct KeybdPrefs { }
static class Settings { public static string SavePath; public static bool Backup; public static bool KeybdSoundRecordActive = true; }
static class SpectreState
{
    public static ReplayData data;
    static bool EmptySaveBlocked, debug_no_encryption;
    public static bool DebugMode;
    public static readonly List<string> Messages = new List<string>();
    static void TriggerMessage(string message) { Messages.Add(message); }
    static void NotifySaveSkipped(string message, string reason) { }
    static string CreateFileName(bool safe_mode = false) => "safe";
    __SAVE__
}
static class LocalizationManager { public static string GetLocalizedText(string key) => key; }
class Patch_Hit { } class Patch_UpdateInputConsumeAsync { } class Patch_PlayerControlUpdate { } class Patch_Won_Update { }
static class SpectrePatchHub { public static void EnsureApplied(string reason, params Type[] types) { } public static void ForceRebuild(Type type) { } }
static class Main { public static Mod mod = new Mod(); public class Mod { public string Path = ""; } }
static class SpectreAPI { public static void RaiseReplaySaved(string path) { } }
static class CloudReplay { public static bool OnReplaySaved(string path, string audio) => false; }
static class Ui { public static class Loc { public static string T(string key) => key; } }
static class ReplayConstants { public const string Extension = ".sprp", ExtensionNoEncrypt = ".sprpj"; }
static class ReplayManager
{
    public static int Saves, Keys, Hits, Cursors;
    public static bool ValidWav, GateHeldAtSave, FailFirst;
    public static string SavedName, SavedAudioName, SavedDirectory, Hash;
    public static byte[] Pcm;
    public static void Reset() { Saves = 0; ValidWav = false; FailFirst = false; Pcm = Array.Empty<byte>(); }
    public static bool SaveReplay(ReplayData data, string path, bool plain, bool embedKeybdSound)
    {
        Saves++; SavedName = data.FileName; SavedAudioName = data.KeybdSoundFileName;
        SavedDirectory = Path.GetDirectoryName(path); Keys = data.KeyEvents.Count; Hits = data.HitSnapshots.Count; Cursors = data.CursorTrack.Count;
        GateHeldAtSave = AudioRecorder.Current.IsCleanupPending;
        string tmp = Path.Combine(SavedDirectory, "tmp.wav");
        if (data.KeybdSoundFileName != null && File.Exists(tmp))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(tmp); }
            catch (IOException) { return true; } // 与产品TryEmbed一致：占用中只拒绝音频嵌入。
            Pcm = bytes.Skip(44).ToArray();
            Hash = Convert.ToHexString(SHA256.HashData(Pcm)).ToLowerInvariant();
            ValidWav = BitConverter.ToInt32(bytes,4) == bytes.Length - 8 && BitConverter.ToInt32(bytes,40) == Pcm.Length && Hash == data.KeybdSoundHash;
            if (ValidWav) data.KeybdAudio = new KeybdAudioBlob { HasData = true };
        }
        return !FailFirst || Saves > 1;
    }
}
""";
}
