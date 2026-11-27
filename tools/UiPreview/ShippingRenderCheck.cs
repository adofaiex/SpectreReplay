using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// Shipping F17–F20: compile production bodies, substituting only Unity/process/clock boundaries.
// No encoder, game, GUI, device or network is started. Missing/extraction/compile errors are failures.
internal static class ShippingRenderCheck
{
    internal static int Run()
    {
        Console.WriteLine("  ── Shipping render F17–F20 (controlled offline boundaries) ──");
        string encoder = SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/VideoEncoder.cs");
        string job = SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/VideoRenderJob.cs");
        string audio = SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/VideoAudioTrack.cs");
        string page = SrcUtil.Source(Driver.ProjectDir, "Pages/RenderPage.cs");
        string render = SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/VideoRender.cs");
        string clock = SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/RenderClock.cs");
        string settings = SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/RenderEncodingSettings.cs");
        int fail = Fixture("F17", () => SubmissionSource(encoder, job) + SubmissionFixture);
        fail += Fixture("F18", () => ProbeSource(encoder, page, render) + ProbeFixture);
        fail += Fixture("F19", () => AudioSource(audio, job) + AudioFixture);
        fail += Fixture("F20", () => RateSource(job, clock, settings) + RateFixture);
        return fail;
    }

    private static ClassDeclarationSyntax Class(string source, string name)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == name);
    private static MethodDeclarationSyntax Method(ClassDeclarationSyntax type, string name)
        => type.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
    private static string Members(ClassDeclarationSyntax type, params string[] names)
        => string.Join("\n", type.Members.Where(member => member switch
        {
            MethodDeclarationSyntax m => names.Contains(m.Identifier.ValueText),
            ClassDeclarationSyntax c => names.Contains(c.Identifier.ValueText),
            EnumDeclarationSyntax e => names.Contains(e.Identifier.ValueText),
            FieldDeclarationSyntax f => f.Declaration.Variables.Any(v => names.Contains(v.Identifier.ValueText)),
            _ => false
        }).Select(m => m.ToFullString()));

    private static string SubmissionSource(string encoder, string job)
    {
        var enc = Class(encoder, "VideoEncoder");
        var capture = Class(job, "VideoRenderJob");
        // Keep the actual coroutine loops at all three call sites, not a reconstructed loop.
        var loops = Method(capture, "Phases").DescendantNodes().OfType<WhileStatementSyntax>()
            .Where(w => w.Condition.ToString().Contains("Flush()", StringComparison.Ordinal)).ToArray();
        if (loops.Length != 3) throw new InvalidOperationException("F17 expected three yielding Flush callers, got " + loops.Length);
        string callers = string.Join("\n", loops.Select((w, i) =>
            "public System.Collections.IEnumerator Feed" + i + "() { " + w.ToFullString() + " yield break; }"));
        var cleanup = Method(capture, "TearDown").DescendantNodes().OfType<TryStatementSyntax>()
            .Single(t => t.Block.ToString().Contains("ReleasePendingFrame()", StringComparison.Ordinal));
        return """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using UnityEngine;
using Stopwatch = FixtureStopwatch;
public sealed partial class EncoderSubject
{
""" + Members(enc, "PendingFrame", "SubmitStallTimeoutMs", "submitStallClock", "TrySubmit", "PumpLoop", "DrainPendingFrames") + "\n}\n"
            + "public sealed partial class JobSubject {\n"
            + Members(capture, "pendingPixels", "pendingRecycle", "Flush", "ReleasePendingFrame", "WaitForBackpressure")
            + "\n" + callers + "\npublic void Cleanup() { " + cleanup.ToFullString() + " }\n}";
    }

    private static string ProbeSource(string encoder, string page, string render)
    {
        var enc = Class(encoder, "VideoEncoder");
        string ui = Method(Class(page, "Pages"), "DrawEncoderNote").ToFullString();
        string start = Method(Class(render, "VideoRender"), "StartRenderInternal").ToFullString();
        string convert = Method(Class(page, "Pages"), "StartConvert").ToFullString();
        return """
using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using Features = SpectreReplay.Features;
using Environment = FixtureEnvironment;
using ThreadPool = ControlledThreadPool;
""" + "public static partial class VideoEncoder {\n"
            + Members(enc, "ProbeRetryMs", "Cooling", "ProbeState", "ProbeSnapshot", "probeSnapshot",
                "ReadProbeSnapshot", "lastWarmUpTick", "lastWarmUpKey", "warmUpActive", "WarmUp")
            + "\n}\npublic static partial class UiSubject {\n" + ui + convert + "\n}\n"
            + "public static partial class RenderSubject {\n" + start + "\n}";
    }

    private static string AudioSource(string audio, string job)
    {
        var capture = Class(job, "VideoRenderJob");
        var phases = Method(capture, "Phases").Body.Statements;
        int captureEnd = phases.Select((s, i) => (s, i)).Single(p => p.s.ToString() == "VideoRender.CaptureInProgress = false;").i;
        int drain = phases.Select((s, i) => (s, i)).First(p => p.i > captureEnd && p.s is WhileStatementSyntax).i;
        string beforeDrain = string.Join("\n", phases.Skip(captureEnd).Take(drain - captureEnd).Select(s => s.ToFullString()));
        return """
using System;
using System.IO;
using System.Collections.Generic;
using SpectreReplay.Features.VideoRender;
using Unity.Collections;
using UnityEngine;
""" + "\nnamespace SpectreReplay.Features.VideoRender {\n"
            + Class(audio, "VideoAudioTrack").ToFullString() + "\n}\n"
            + "public sealed partial class AudioJobSubject {\n"
            + "public void CaptureEnded() {\n" + beforeDrain + "\n}\n"
            + Method(capture, "StartFinalizeOnWorker").ToFullString() + "\n}";
    }

    private static string RateSource(string job, string clock, string settings)
    {
        var capture = Class(job, "VideoRenderJob");
        var phases = Method(capture, "Phases");
        LocalDeclarationStatementSyntax Local(string name) => phases.DescendantNodes()
            .OfType<LocalDeclarationStatementSyntax>().Single(s => s.Declaration.Variables
                .Any(v => v.Identifier.ValueText == name));
        ExpressionStatementSyntax Call(string name) => phases.DescendantNodes()
            .OfType<ExpressionStatementSyntax>().First(s => s.Expression is InvocationExpressionSyntax call
                && call.Expression.ToString() == name);
        var release = Method(capture, "ReleaseGameControl");
        string Fields() => string.Concat(capture.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText.StartsWith("pinnedVideo", StringComparison.Ordinal)
                || v.Identifier.ValueText is "videoRealClock" or "controlReleased")).Select(f => f.ToFullString()));
        string VideoCall(MethodDeclarationSyntax method, string name) => method.DescendantNodes()
            .OfType<ExpressionStatementSyntax>().FirstOrDefault(s => s.Expression is InvocationExpressionSyntax call
                && call.Expression.ToString() == name)?.ToFullString() ?? "";
        // Execute production ownership and its real start/teardown call sites. The boundary
        // checks API/state ownership, not native decoding or image synchronization.
        return "using System; using System.Collections.Generic; using UnityEngine; using SpectreReplay.Features.VideoRender; using Settings = SpectreReplay.Settings;\n"
            + "namespace SpectreReplay.Features.VideoRender {\n"
            + Class(clock, "RenderClock").ToFullString() + "\n"
            + Class(settings, "RenderEncodingSettings").ToFullString() + "\n}\n"
            + "public sealed partial class RateSubject {\n" + Fields()
            + Method(capture, "PinVideoPlayers").ToFullString() + Method(capture, "UnpinVideoPlayers").ToFullString()
            + "\npublic void StartClock() { " + Local("fps").ToFullString() + Local("clock").ToFullString()
            + " snapshotClock = clock; " + VideoCall(phases, "PinVideoPlayers") + " }\n"
            + "public void EndCapture() { " + VideoCall(Method(capture, "TearDown"), "ReleaseGameControl") + " }\n"
            + "private void ReleaseGameControl() { "
            + string.Concat(release.Body.Statements.Take(2).Select(s => s.ToFullString()))
            + VideoCall(release, "UnpinVideoPlayers") + " }\n"
            + "public void AdvanceFrame() { RenderClock clock = snapshotClock; "
            + Call("clock.Advance").ToFullString() + " }\n}";
    }

    private static int Fixture(string label, Func<string> source)
    {
        try
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("ShippingRender_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source()) }, trusted,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)));
            var fixture = Assembly.Load(image.ToArray()).GetType("Fixture");
            string[] results = (string[])fixture.GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            if (results.Length == 0) throw new InvalidOperationException("zero assertions");
            foreach (string result in results) Console.WriteLine("    " + result.Substring(0, 2) + label + " " + result.Substring(2));
            return results.Count(s => s.StartsWith("✗", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Console.WriteLine("    ✗ " + label + " fixture failed: " + (ex.InnerException ?? ex).Message);
            return 1;
        }
    }

    private const string SubmissionFixture = """
namespace UnityEngine { public sealed class WaitForEndOfFrame { } }
public static class UnscaledDeltaPin { public static void BeginBackpressureWait() { } }
public sealed class FixtureStopwatch
{
    public static long Now;
    private readonly long start = Now;
    public static FixtureStopwatch StartNew() => new();
    public long ElapsedMilliseconds => Now - start;
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMilliseconds);
}
public sealed partial class EncoderSubject
{
    private sealed class Pipe
    {
        public bool HasExited;
        public readonly Input StandardInput = new();
    }
    private sealed class Input { public readonly Output BaseStream = new(); }
    private sealed class Output
    {
        public bool Fail;
        public void Write(byte[] data, int offset, int length) { if (Fail) throw new System.IO.IOException("controlled stdin failure"); }
    }
    private bool hdr10;
    private int width = 1, height = 1;
    private int ConvertHdrFrame(byte[] src, int length, byte[] dst) => length;
    private readonly Pipe pipe = new();
    private Exception pumpFailure;
    // Substitute only the bounded queue wait, recording its timeout so regressions cannot hang CI.
    private sealed class QueueBoundary
    {
        internal readonly Queue<PendingFrame> Items = new();
        internal int LastWait;
        internal bool TryTake(out PendingFrame frame)
        {
            frame = Items.Count == 0 ? null : Items.Dequeue();
            return frame != null;
        }
        internal IEnumerable<PendingFrame> GetConsumingEnumerable()
        { while (TryTake(out PendingFrame frame)) yield return frame; }
        internal bool TryAdd(PendingFrame frame, int milliseconds)
        {
            LastWait = milliseconds;
            if (Items.Count != 0)
            {
                if (milliseconds != 0) throw new InvalidOperationException("blocking queue wait: " + milliseconds);
                return false;
            }
            Items.Enqueue(frame); return true;
        }
    }
    private readonly QueueBoundary queue = new();
    private string StderrSuffix() => "";
    public int LastWait => queue.LastWait;
    public int Count => queue.Items.Count;
    public void Fill() => queue.Items.Enqueue(new PendingFrame { Data = new byte[] { 99 } });
    public void FailPump() { pumpFailure = new System.IO.IOException("controlled pump failure"); }
    public bool ExercisePumpFailure(Action<byte[]> recycle, byte[] a, byte[] b)
    {
        queue.Items.Enqueue(new PendingFrame { Data = a, Length = 1, Repeat = 1, Recycle = recycle });
        queue.Items.Enqueue(new PendingFrame { Data = b, Length = 1, Repeat = 1, Recycle = recycle });
        pipe.StandardInput.BaseStream.Fail = true;
        PumpLoop();
        bool retained = queue.Items.Count == 1 && pumpFailure != null;
        DrainPendingFrames(); DrainPendingFrames();
        return retained && queue.Items.Count == 0;
    }
    public void Pump(List<int> order)
    {
        var f = queue.Items.Dequeue();
        order.Add(f.Data[0]);
        f.Recycle?.Invoke(f.Data);
    }
}
public sealed class FramesBoundary
{
    private readonly Queue<byte[]> ready = new();
    public readonly Dictionary<byte[], int> Returns = new();
    public byte[] HeldFrame;
    public int Taken;
    public byte[] Add(byte n) { var b = new[] { n }; ready.Enqueue(b); return b; }
    public bool TryTake(out int index, out byte[] pixels, out bool failed)
    {
        index = Taken; failed = false; pixels = null;
        if (ready.Count == 0) return false;
        pixels = ready.Dequeue(); Taken++; return true;
    }
    public void Recycle(byte[] pixels)
    { Returns.TryGetValue(pixels, out int n); Returns[pixels] = n + 1; }
    public void RecycleOrHold(byte[] pixels)
    { if (!ReferenceEquals(pixels, HeldFrame)) Recycle(pixels); }
    public int Returned(byte[] p) => Returns.TryGetValue(p, out int n) ? n : 0;
}
public static class VideoRender
{
    public sealed class State { public int FramesDone, FramesTotal = 100; public double EtaSeconds; }
    public static readonly State Current = new();
}
public static class Debug { public static void LogWarning(object o) { } public static void LogException(Exception e) { } }
public sealed partial class JobSubject
{
    public readonly FramesBoundary frames = new();
    public readonly EncoderSubject encoder = new();
    private readonly byte[] blackout = new byte[1];
    public bool cancelRequested;
    private int failedReadbacks, delivered;
    private FixtureStopwatch captureClock;
    public int Delivered => delivered;
    public bool Owns(byte[] p) => ReferenceEquals(pendingPixels, p);
}
public static class Fixture
{
    public static string[] Run()
    {
        var r = new List<string>();
        void Check(bool ok, string name) => r.Add((ok ? "✓ " : "✗ ") + name);
        FixtureStopwatch.Now = 0;
        for (int call = 0; call < 3; call++)
        {
            var j = new JobSubject(); j.encoder.Fill(); var a = j.frames.Add(1); var b = j.frames.Add(2);
            var feed = call == 0 ? j.Feed0() : call == 1 ? j.Feed1() : j.Feed2();
            bool yielded = false;
            try { yielded = feed.MoveNext() && feed.Current is WaitForEndOfFrame; } catch { }
            Check(yielded && j.encoder.LastWait == 0 && j.Delivered == 0 && j.frames.Taken == 1
                && j.Owns(a) && j.frames.Returned(a) == 0, "full queue caller " + call + " returns/yields immediately, retaining first buffer");
            j.cancelRequested = true;
            bool canceled = false;
            try { feed.MoveNext(); } catch (OperationCanceledException) { canceled = true; }
            j.Cleanup(); j.Cleanup();
            Check(canceled && j.frames.Returned(a) == 1 && j.frames.Returned(b) == 0
                && j.Delivered == 0 && j.encoder.Count == 1, "cancel reclaims only unsubmitted buffer once (caller " + call + ")");
        }
        var ordered = new JobSubject(); ordered.encoder.Fill(); var x = ordered.frames.Add(1); var y = ordered.frames.Add(2);
        var e = ordered.Feed0(); var order = new List<int>();
        bool correct = false;
        try
        {
            bool first = e.MoveNext(); ordered.encoder.Pump(order);
            bool second = e.MoveNext();
            bool held = ordered.Owns(y) && ordered.frames.Returned(x) == 0;
            ordered.encoder.Pump(order);
            bool done = !e.MoveNext(); ordered.encoder.Pump(order); ordered.Cleanup(); ordered.Cleanup();
            correct = first && second && held && done && ordered.Delivered == 2
                && order.SequenceEqual(new[] { 99, 1, 2 }) && ordered.frames.Returned(x) == 1 && ordered.frames.Returned(y) == 1;
        }
        catch { }
        Check(correct, "FIFO preserved across yields; accepted buffers recycled only by pump, exactly once");
        for (int call = 0; call < 3; call++)
        {
            var j = new JobSubject(); var heldPixels = j.frames.Add(8);
            j.frames.HeldFrame = heldPixels;
            var feed = call == 0 ? j.Feed0() : call == 1 ? j.Feed1() : j.Feed2();
            bool retained = false;
            try
            {
                bool submitted = !feed.MoveNext() && j.Delivered == 1 && j.encoder.Count == 1
                    && !j.Owns(heldPixels) && j.frames.Returned(heldPixels) == 0;
                j.encoder.Pump(new List<int>()); j.Cleanup(); j.Cleanup();
                retained = submitted && j.frames.Returned(heldPixels) == 0;
                j.frames.HeldFrame = null; j.frames.Recycle(heldPixels);
                retained &= j.frames.Returned(heldPixels) == 1;
            }
            catch { }
            Check(retained, "pump retains fallback-source frame until its owner releases it (caller " + call + ")");
        }
        var cancelMixed = new JobSubject();
        var accepted = cancelMixed.frames.Add(6); var waiting = cancelMixed.frames.Add(7);
        var cancelFeed = cancelMixed.Feed0(); bool mixedOk = false;
        try
        {
            bool paused = cancelFeed.MoveNext();
            cancelMixed.cancelRequested = true;
            bool observedCancel = false;
            try { cancelFeed.MoveNext(); } catch (OperationCanceledException) { observedCancel = true; }
            cancelMixed.Cleanup(); cancelMixed.Cleanup();
            bool owned = cancelMixed.frames.Returned(accepted) == 0 && cancelMixed.frames.Returned(waiting) == 1;
            cancelMixed.encoder.Pump(new List<int>());
            mixedOk = paused && observedCancel && owned && cancelMixed.frames.Returned(accepted) == 1
                && cancelMixed.frames.Returned(waiting) == 1 && cancelMixed.Delivered == 1;
        }
        catch { }
        Check(mixedOk, "cancel with accepted and pending frames reclaims each once by its sole owner");
        var timeout = new EncoderSubject(); timeout.Fill(); FixtureStopwatch.Now = 0;
        bool timed = false;
        try
        {
            bool full = !timeout.TrySubmit(new byte[1], 1, 1, null, () => false);
            FixtureStopwatch.Now = 119999;
            bool still = !timeout.TrySubmit(new byte[1], 1, 1, null, () => false);
            FixtureStopwatch.Now = 120000;
            try { timeout.TrySubmit(new byte[1], 1, 1, null, () => false); }
            catch (TimeoutException) { timed = full && still; }
        }
        catch { }
        Check(timed, "120s continuous wall-clock stall expires in three polls, independent of total frames/fps");
        var reset = new EncoderSubject(); reset.Fill(); FixtureStopwatch.Now = 0;
        bool resetOk = false;
        try
        {
            reset.TrySubmit(new byte[1], 1, 1, null, () => false);
            FixtureStopwatch.Now = 119999; reset.Pump(new List<int>());
            reset.TrySubmit(new byte[1], 1, 1, null, () => false);
            FixtureStopwatch.Now = 999999;
            resetOk = !reset.TrySubmit(new byte[1], 1, 1, null, () => false);
        }
        catch { }
        Check(resetOk, "successful handoff resets continuous stall clock");
        var callbacks = new Dictionary<byte[], int>(); var pa = new byte[] { 3 }; var pb = new byte[] { 4 };
        void Recycle(byte[] pixels) { callbacks.TryGetValue(pixels, out int n); callbacks[pixels] = n + 1; }
        bool drained = new EncoderSubject().ExercisePumpFailure(Recycle, pa, pb);
        Check(drained && callbacks.TryGetValue(pa, out int returnedA) && returnedA == 1
            && callbacks.TryGetValue(pb, out int returnedB) && returnedB == 1,
            "real pump finally reclaims active failed write; real repeated drain reclaims queued frames once");
        var failedJob = new JobSubject(); var failedPixels = failedJob.frames.Add(5); failedJob.encoder.FailPump();
        bool failed = false;
        try { failedJob.Feed0().MoveNext(); } catch (InvalidOperationException) { failed = true; }
        failedJob.Cleanup(); failedJob.Cleanup();
        Check(failed && failedJob.frames.Returned(failedPixels) == 1 && failedJob.encoder.Count == 0,
            "pump failure leaves unsubmitted frame owned by job until actual cleanup, exactly once");
        return r.ToArray();
    }
}
""";

    private const string ProbeFixture = """
public static class FixtureEnvironment { public static int TickCount = 1; }
public static class ControlledThreadPool
{
    private static readonly Queue<WaitCallback> pending = new();
    public static bool QueueUserWorkItem(WaitCallback work) { pending.Enqueue(work); return true; }
    public static void Drain() { while (pending.Count > 0) pending.Dequeue()(null); }
    public static int Count => pending.Count;
}
namespace SpectreReplay
{
    public static class Settings
    {
        public static bool RenderOn = true, Render10Bit, RenderHdr10;
        public static int RenderOutputFormat, RenderVideoCodec, RenderHardware, RenderCaptureMode;
    }
    namespace Ui { public static class Loc { public static string T(string key) => key; } }
    namespace Features.VideoRender { public static class VideoRender { public static JobBoundary Job; } }
}
public sealed class JobBoundary { public string ActualEncoder; }
public static class Mathf { public static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v)); }
public static class Debug
{
    public static int Warnings;
    public static void Log(object o) { }
    public static void LogWarning(object o) { Warnings++; }
    public static void LogError(object o) { Warnings++; }
}
public static class EncoderCatalog { public static void EnsureLoaded(string exe) { } }
public static partial class VideoEncoder
{
    public enum OutContainer { Mp4, Mkv, WebM, Mov }
    public enum VideoCodec { H264, H265, Av1 }
    public enum HardwareBackend { Auto, Nvenc, Qsv, Amf, Cpu }
    public static readonly object ProbeLock = new();
    public static int ProcessCalls;
    public static bool Works = true, ThrowLocate;
    public static string LocateEncoder()
    {
        if (!Fixture.InWorker) throw new InvalidOperationException("UI performed encoder location");
        if (ThrowLocate) throw new InvalidOperationException("controlled pre-start error");
        return "fake-ffmpeg";
    }
    public static bool VerifyEncoder(string exe)
    {
        if (!Fixture.InWorker) throw new InvalidOperationException("UI performed synchronous -version");
        lock (ProbeLock) { ProcessCalls++; return Works; }
    }
    public static string DescribeEncoder(OutContainer c, VideoCodec v, HardwareBackend h, bool t, bool hdr)
    {
        if (!Fixture.InWorker) throw new InvalidOperationException("UI resolved encoder under probe lock");
        return "libx264 (CPU)";
    }
}
public static class Gui
{
    public static readonly object DimCol = new(), OkCol = new(), BadCol = new();
    public static string LastKey, LastValue;
    public static void KeyValue(string key, string value, object color) { LastKey = key; LastValue = value; }
}
public static class OnGui { public static void Note(string key) { } }
public static class Loc
{
    public static string T(string key) => key;
    public static string F(string key, params object[] args) => key;
}
public static class Status { public static string Last; public static void Warn(string key) { Last = key; } }
public static class RenderOutputPolicy { public static bool Hdr10Enabled(bool h, int c, int v, int f) => h; }
public static class VideoConverter
{
    public sealed class ConvertState { public bool Running; }
    public static ConvertState Start(string dir, VideoEncoder.OutContainer c, string path) => new();
}
public static class LocalizationManager { public static string GetLocalizedText(string key) => key; }
public static class SpectreState { public static ReplayBoundary data = new(); }
public sealed class ReplayBoundary { public readonly List<int> HitSnapshots = new() { 1 }; }
public sealed class Controller { public static Controller instance = new(); }
public static class ADOBase { public static Level lm = new(); public static Editor editor = new(); }
public sealed class Level { public List<int> listFloors = new() { 1, 2 }; }
public sealed class Editor { public List<int> floors = new() { 1, 2 }; }
public static class scrController { public static Controller instance => Controller.instance; }
public static class Settings { public static bool RenderOn => SpectreReplay.Settings.RenderOn; }
public static class SpectreAPI { public static void RaiseRenderStarted() { } }
public static class VideoRenderJob { public static object Launch(string path, object range, object settings) => new(); }
public static partial class RenderSubject
{
    public enum NotifType { Warning }
    public enum Phase { Restarting }
    public sealed class RenderRange { }
    public static object Job, CpuRetrySettings, PendingRange;
    public static Phase CurrentPhase;
    public static string Message;
    public static void TriggerMessage(string text, float extra = 0, NotifType type = NotifType.Warning) { Message = text; }
    public static void ResetLeakedPins() { }
    public static void ResetSnapshot() { }
    public static void Start() => StartRenderInternal(new());
}
public static partial class UiSubject
{
    public static int RenderOutputFormat, RenderCaptureMode, RenderVideoCodec, RenderHardware, RenderRateControl, ConvertTarget;
    public static bool RenderHdr10, Render10Bit;
    public static string RenderOutputDir = System.IO.Directory.GetCurrentDirectory(), SavePath;
    private static VideoConverter.ConvertState _convertState;
    public static void Draw() => DrawEncoderNote();
    public static void Convert() => StartConvert();
}
public static class Fixture
{
    public static bool InWorker;
    private static void Drain() { InWorker = true; try { ControlledThreadPool.Drain(); } finally { InWorker = false; } }
    public static string[] Run()
    {
        var r = new List<string>();
        void Check(bool ok, string name) => r.Add((ok ? "✓ " : "✗ ") + name);
        VideoEncoder.WarmUp(); UiSubject.Draw(); RenderSubject.Start();
        Check(VideoEncoder.ReadProbeSnapshot().State == VideoEncoder.ProbeState.Pending && VideoEncoder.ProcessCalls == 0
            && Gui.LastValue == "render.ffmpeg.probing" && RenderSubject.Job == null
            && RenderSubject.Message == "render.ffmpeg.probing" && Debug.Warnings == 0,
            "cold UI/start returns Pending without process calls or swallowed blocking exceptions");
        Drain(); UiSubject.Draw(); RenderSubject.Start();
        Check(VideoEncoder.ReadProbeSnapshot().State == VideoEncoder.ProbeState.Ready && RenderSubject.Job != null
            && Gui.LastValue == "render.ffmpeg.ready" && VideoEncoder.ProcessCalls == 1 && Debug.Warnings == 0,
            "background publishes Ready; UI and pre-start consume cached path");
        var locked = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (VideoEncoder.ProbeLock) { locked.Set(); release.Wait(); } }); holder.Start(); locked.Wait();
        bool responsive = false;
        var reader = new Thread(() => { try { VideoEncoder.ReadProbeSnapshot(); UiSubject.Draw(); responsive = Debug.Warnings == 0; } catch { } });
        reader.Start(); bool completedWhileLocked = reader.Join(1000); release.Set(); holder.Join(); reader.Join();
        Check(completedWhileLocked && responsive, "UI snapshot/draw does not wait while background owns ProbeLock");
        FixtureEnvironment.TickCount += 30001; VideoEncoder.Works = false; VideoEncoder.WarmUp();
        RenderSubject.Job = null; RenderSubject.Start();
        Check(VideoEncoder.ReadProbeSnapshot().State == VideoEncoder.ProbeState.Ready && RenderSubject.Job != null
            && VideoEncoder.ProcessCalls == 1 && ControlledThreadPool.Count == 1,
            "refresh preserves verified Ready path so automatic CPU retry can launch without waiting");
        Drain(); UiSubject.Draw();
        int failedCalls = VideoEncoder.ProcessCalls; VideoEncoder.WarmUp();
        Check(VideoEncoder.ReadProbeSnapshot().State == VideoEncoder.ProbeState.Failed && Gui.LastValue == "render.ffmpeg.missing"
            && ControlledThreadPool.Count == 0 && failedCalls == 2, "failed probe is visible and cooldown prevents repeated process creation");
        FixtureEnvironment.TickCount += 30001; VideoEncoder.Works = true; VideoEncoder.WarmUp(); Drain();
        Check(VideoEncoder.ReadProbeSnapshot().State == VideoEncoder.ProbeState.Ready && VideoEncoder.ProcessCalls == failedCalls + 1,
            "cooldown retry recovers after encoder repair");
        FixtureEnvironment.TickCount += 30001; VideoEncoder.ThrowLocate = true; VideoEncoder.WarmUp(); Drain();
        Check(VideoEncoder.ReadProbeSnapshot().State == VideoEncoder.ProbeState.Failed && Debug.Warnings == 1,
            "pre-start location exception publishes Failed, releasing running gate");
        VideoEncoder.ThrowLocate = false; FixtureEnvironment.TickCount += 30001;
        SpectreReplay.Settings.RenderOn = false; VideoEncoder.WarmUp();
        Check(ControlledThreadPool.Count == 0, "render switch off does not schedule render warmup");
        UiSubject.Convert(); bool queued = ControlledThreadPool.Count == 1; Drain(); UiSubject.Convert();
        Check(queued && VideoEncoder.ReadProbeSnapshot().State == VideoEncoder.ProbeState.Ready,
            "explicit conversion can asynchronously probe even when rendering is disabled");
        return r.ToArray();
    }
}
""";

    private const string AudioFixture = """
namespace Unity.Collections
{
    public enum Allocator { Temp, Persistent }
    public enum NativeArrayOptions { ClearMemory }
    public struct NativeArray<T> : IDisposable
    {
        private T[] data;
        public NativeArray(int n, Allocator a, NativeArrayOptions o = default) { data = new T[n]; }
        public bool IsCreated => data != null;
        public int Length => data.Length;
        public void CopyTo(T[] dst) { Array.Copy(data, dst, data.Length); }
        public void Dispose() { data = null; }
    }
}
namespace UnityEngine
{
    public enum AudioSpeakerMode { Mono, Stereo, Quad, Surround, Mode5point1, Mode7point1 }
    public struct AudioConfiguration { public int dspBufferSize, numRealVoices, numVirtualVoices, sampleRate; public AudioSpeakerMode speakerMode; }
    public static class AudioSettings
    {
        public static int outputSampleRate => 48000;
        public static AudioSpeakerMode speakerMode => AudioSpeakerMode.Stereo;
        public static double dspTime => 1;
        public static AudioConfiguration GetConfiguration() => new();
    }
    public static class AudioRenderer
    {
        public static int Starts, Stops, Owner;
        private static void MainOnly() { if (System.Threading.Thread.CurrentThread.ManagedThreadId != Owner) throw new Exception("Unity audio on worker"); }
        public static bool Start() { MainOnly(); Starts++; return true; }
        public static bool Stop() { MainOnly(); Stops++; return true; }
        public static int GetSampleCountForCaptureFrame() { MainOnly(); return 2; }
        public static bool Render(Unity.Collections.NativeArray<float> data) { MainOnly(); return true; }
    }
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
    }
}
namespace SpectreReplay.Features.VideoRender
{
    public static class UnscaledDeltaPin { public static double RealDsp() => 1; }
}
public static class VideoRender { public static bool CaptureInProgress = true; }
public sealed class Frames { public int InFlight => 0; }
public sealed class VideoEncoder
{
    public bool ReadFileOnWorker;
    public void FinishVideo(Func<bool> canceled) { }
    public void MuxWith(string wav, double offset, Func<bool> canceled)
    {
        if (UnityEngine.AudioRenderer.Stops != 1) throw new Exception("AudioRenderer still owns output during mux");
        if (System.Threading.Thread.CurrentThread.ManagedThreadId == UnityEngine.AudioRenderer.Owner) throw new Exception("mux on main");
        using var f = new FileStream(wav, FileMode.Open, FileAccess.Read, FileShare.Read);
        ReadFileOnWorker = f.Length == 60;
    }
}
public sealed partial class AudioJobSubject
{
    internal VideoAudioTrack mixdown;
    public readonly VideoEncoder encoder = new();
    private readonly Frames frames = new();
    public string wavPath;
    private double muxAudioOffsetSec;
    private bool cancelRequested;
    private System.Threading.Thread finalizeWorker;
    private volatile bool finalizeDone;
    private volatile Exception finalizeError;
    private volatile bool finalizeErrorFromMux;
    public bool Finish()
    { StartFinalizeOnWorker(); return finalizeWorker.Join(2000) && finalizeDone && finalizeError == null && encoder.ReadFileOnWorker; }
}
public static class Fixture
{
    public static string[] Run()
    {
        var r = new List<string>();
        void Check(bool ok, string name) => r.Add((ok ? "✓ " : "✗ ") + name);
        UnityEngine.AudioRenderer.Owner = System.Threading.Thread.CurrentThread.ManagedThreadId;
        string dir = Path.Combine(Directory.GetCurrentDirectory(), ".tmp_ship_20261126", "render", "fixtures");
        Directory.CreateDirectory(dir); string path = Path.Combine(dir, "mix-" + Guid.NewGuid().ToString("N") + ".wav");
        var audio = new VideoAudioTrack(path, 60);
        try
        {
            audio.CaptureFrame(); var job = new AudioJobSubject { mixdown = audio, wavPath = path };
            job.CaptureEnded();
            Check(UnityEngine.AudioRenderer.Stops == 1 && !VideoRender.CaptureInProgress,
                "capture-end production slice seals/stops audio before any readback or encoder drain");
            audio.Seal();
            Check(job.Finish(), "actual finalize lambda reads sealed audio off main before track Dispose");
            byte[] file = File.ReadAllBytes(path);
            bool noAppend = true;
            try { audio.CaptureFrame(); } catch { noAppend = false; }
            Check(noAppend && new FileInfo(path).Length == file.Length, "sealed capture cannot append samples while worker owns file");
            audio.Dispose(); audio.Dispose();
            Check(UnityEngine.AudioRenderer.Starts == 1 && UnityEngine.AudioRenderer.Stops == 1 && file.Length == 60
                && BitConverter.ToUInt32(file, 40) == 16, "Seal/Dispose idempotent: exactly one Stop and valid retained WAV");
        }
        finally { audio.Dispose(); File.Delete(path); }
        return r.ToArray();
    }
}
""";

    private const string RateFixture = """
namespace SpectreReplay
{
    public static class Settings
    {
        public static int RenderCaptureMode, RenderWidth, RenderHeight, RenderFps = 60, RenderEncoderMode, RenderQualityMode;
        public static int RenderVideoCodec, RenderOutputFormat, RenderRateControl, RenderCrf, RenderCqp, RenderBitrateMbps, RenderPreset;
        public static int RenderHardware, RenderAudioOffsetMs;
        public static bool RenderHdr10, Render10Bit, RenderCursorOn;
        public static float RenderTailSeconds;
    }
    namespace Features.VideoRender
    {
        public static class VideoEncoder { public enum HardwareBackend { Auto, Nvenc, Qsv, Amf, Cpu } }
        public static class RenderOutputPolicy { public static bool Hdr10Enabled(bool h, int s, int c, int f) => h; }
    }
}
namespace SpectreReplay.Ui
{
    public static class Loc
    {
        public static string T(string key) => key;
        public static string F(string key, params object[] args) => key;
    }
}
namespace UnityEngine
{
    public enum FindObjectsInactive { Exclude, Include }
    public enum FindObjectsSortMode { None }
    public static class Debug
    {
        public static void Log(string text) { }
        public static void LogException(Exception exception) { }
    }
}
namespace UnityEngine.Video
{
    public enum VideoTimeUpdateMode { DSPTime, GameTime, UnscaledGameTime }
    public sealed class VideoPlayer
    {
        public bool Active = true, isPrepared = true, isPlaying, HasPlayed;
        public int Plays, Pauses, SpeedWrites, SeekWrites;
        public bool canSetPlaybackSpeed;
        public VideoTimeUpdateMode timeUpdateMode;
        private float speed = 1;
        private double position;
        public float playbackSpeed { get => speed; set { speed = value; SpeedWrites++; } }
        public double time { get => position; set { position = value; SeekWrites++; } }
        public void Play() { Plays++; isPlaying = true; }
        public void Pause() { Pauses++; isPlaying = false; }
        // Controlled game boundary, matching scrVfxPlus's prepared/offset/hasPlayed gate.
        public void NativeStart(double songPosition, double countdownEnd, double offset, float pitch)
        {
            if (!Active || isPlaying || HasPlayed || !isPrepared || songPosition < countdownEnd - offset) return;
            Play(); HasPlayed = true; playbackSpeed = pitch; time = songPosition - countdownEnd + offset;
        }
    }
}
public static class Mathf
{
    public static float Clamp(float x, float a, float b) => Math.Max(a, Math.Min(b, x));
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float Abs(float x) => Math.Abs(x);
}
public sealed partial class RateSubject
{
    private readonly RenderEncodingSettings options = new();
    private RenderClock snapshotClock;
    public static UnityEngine.Video.VideoPlayer[] Players = Array.Empty<UnityEngine.Video.VideoPlayer>();
    private static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort)
    {
        var found = new List<T>();
        foreach (var player in Players)
            if (player == null || inactive == FindObjectsInactive.Include || player.Active) found.Add((T)(object)player);
        return found.ToArray();
    }
    public double Elapsed => snapshotClock.Time;
}
public static class Fixture
{
    public static string[] Run()
    {
        var r = new List<string>();
        void Check(bool ok, string name) => r.Add((ok ? "✓ " : "✗ ") + name);
        var waiting = new UnityEngine.Video.VideoPlayer { timeUpdateMode = UnityEngine.Video.VideoTimeUpdateMode.DSPTime };
        var playing = new UnityEngine.Video.VideoPlayer { timeUpdateMode = UnityEngine.Video.VideoTimeUpdateMode.UnscaledGameTime,
            isPlaying = true, playbackSpeed = 1.5f, time = 7 };
        var inactive = new UnityEngine.Video.VideoPlayer { timeUpdateMode = UnityEngine.Video.VideoTimeUpdateMode.DSPTime,
            Active = false, isPrepared = false };
        var gameTime = new UnityEngine.Video.VideoPlayer { timeUpdateMode = UnityEngine.Video.VideoTimeUpdateMode.GameTime };
        RateSubject.Players = new[] { waiting, playing, inactive, gameTime, null };
        Settings.RenderFps = 60; var job = new RateSubject();
        Settings.RenderFps = 120; job.StartClock();
        Check(waiting.timeUpdateMode == UnityEngine.Video.VideoTimeUpdateMode.GameTime
            && playing.timeUpdateMode == UnityEngine.Video.VideoTimeUpdateMode.GameTime,
            "active backgrounds use native GameTime/captureFramerate, not DSP or real-time rate matching");
        Check(inactive.timeUpdateMode == UnityEngine.Video.VideoTimeUpdateMode.GameTime && inactive.Plays == 0,
            "inactive/unprepared background is clock-pinned without starting preparation or playback");
        Check(waiting.Plays == 0 && gameTime.Plays == 0 && playing.Plays == 0 && playing.isPlaying
            && playing.playbackSpeed == 1.5f && playing.time == 7 && playing.SpeedWrites == 1 && playing.SeekWrites == 1,
            "pinning leaves native playback, pause, song pitch and video phase untouched");
        waiting.NativeStart(1, 4, 0.5, 1.25f);
        Check(!waiting.isPlaying && !waiting.HasPlayed,
            "waiting background does not start before the game's countdown/vidOffset threshold");
        waiting.NativeStart(4, 4, 0.5, 1.25f);
        Check(waiting.HasPlayed && waiting.Plays == 1 && waiting.time == 0.5 && waiting.playbackSpeed == 1.25f,
            "native prepared/offset gate still starts video and initializes phase/pitch");
        for (int i = 0; i < 30; i++) job.AdvanceFrame();
        Settings.RenderFps = 30;
        for (int i = 0; i < 30; i++) job.AdvanceFrame();
        Check(Math.Abs(job.Elapsed - 1) < 0.000001,
            "production clock/caller keeps job 60fps snapshot when Settings changes before start and mid-capture");
        job.EndCapture();
        Check(waiting.timeUpdateMode == UnityEngine.Video.VideoTimeUpdateMode.DSPTime
            && playing.timeUpdateMode == UnityEngine.Video.VideoTimeUpdateMode.UnscaledGameTime
            && inactive.timeUpdateMode == UnityEngine.Video.VideoTimeUpdateMode.DSPTime
            && gameTime.timeUpdateMode == UnityEngine.Video.VideoTimeUpdateMode.GameTime,
            "real teardown call restores each original video clock mode, including inactive and GameTime");
        Check(waiting.isPlaying && waiting.Pauses == 0 && waiting.playbackSpeed == 1.25f && waiting.time == 0.5
            && playing.isPlaying && playing.playbackSpeed == 1.5f && playing.SpeedWrites == 1 && playing.SeekWrites == 1,
            "teardown never pauses a background started by the game or resets song pitch/phase");
        RateSubject.Players = Array.Empty<UnityEngine.Video.VideoPlayer>();
        var empty = new RateSubject(); bool emptyOk = true;
        try { empty.StartClock(); empty.EndCapture(); } catch { emptyOk = false; }
        Check(emptyOk && empty.Elapsed == 0, "levels without video keep the normal capture lifecycle");
        return r.ToArray();
    }
}
""";
}
