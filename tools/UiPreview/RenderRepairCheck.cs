using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// 渲染修复的离线行为检查：动态编译真实纯 helper，安排确定性交错并执行假进程。
internal static class RenderRepairCheck
{
    internal static int Run()
    {
        Console.WriteLine("  ── 渲染修复：所有权/取消/MOV 回退 ──");
        int fail = 0;
        fail += RunFixture("FrameBufferOwnership", Real("Features/VideoRender/Pipeline/FrameBufferOwnership.cs"), FrameBufferFixture);
        fail += RunFixture("EncoderOwnership", Real("Features/VideoRender/Pipeline/EncoderPreStartOwnership.cs"), EncoderOwnershipFixture);
        fail += RunFixture("Cancellation", Real("Features/VideoRender/Pipeline/CancellableProcessRunner.cs"), Real("Features/VideoRender/Pipeline/CancellableWait.cs"), CancellationFixture);
        fail += RunFixture("MovFallback", Real("Features/VideoRender/Pipeline/MovConversionFallback.cs"), MovFallbackFixture);
        fail += RunFixture("HasActiveWorker", HasActiveWorkerSource(Real("Features/VideoRender/Pipeline/VideoEncoder.cs")), HasActiveWorkerFixture);
        string job = Real("Features/VideoRender/Pipeline/VideoRenderJob.cs");
        string ownershipSource = Real("Features/VideoRender/Pipeline/EncoderPreStartOwnership.cs");
        fail += RunFixture("JobCallers", ownershipSource, JobCallerSource(job), JobCallerFixture);
        string cleanup = job.Substring(job.IndexOf("private void DeferCleanupAfterWorker(", StringComparison.Ordinal));
        bool wired = job.Contains("enc.FinishVideo(() => cancelRequested);", StringComparison.Ordinal)
            && job.Contains("enc.MuxWith(wav, audioOffset, () => cancelRequested);", StringComparison.Ordinal)
            && job.Contains("try { frames?.Dispose(); }", StringComparison.Ordinal)
            && !cleanup.Contains("Frames?.Dispose", StringComparison.Ordinal)
            && !cleanup.Contains("frames?.Dispose", StringComparison.Ordinal);
        Console.WriteLine("    " + (wired ? "✓ " : "✗ ") + "RR08 取消贯穿编码与mux，Unity帧源不在后台销毁");
        if (!wired) fail++;
        bool ownershipWired = job.Contains("encoderOwnership.StartPreStart(() => _encoderPreStart.Start());", StringComparison.Ordinal)
            && job.Contains("encoderOwnership.ClaimPublished(value => encoder = value);", StringComparison.Ordinal)
            && job.Contains("encoderOwnership.Close(encoder, out VideoEncoder closingEncoder,", StringComparison.Ordinal)
            && job.Contains("encoderOwnership.RetainForDeferredCleanup(candidate)", StringComparison.Ordinal)
            && job.Contains("encoderOwnership.HasDeferredCleanupCandidate", StringComparison.Ordinal)
            && job.Contains("EncoderPreStartOwnership<VideoEncoder>.MustDeferCleanup(", StringComparison.Ordinal)
            && job.Contains("EncoderPreStartOwnership<VideoEncoder>.CreateCleanupPlan(", StringComparison.Ordinal)
            && job.Contains("cleanupPlan.DeferredEncoder", StringComparison.Ordinal)
            && job.Contains("cleanupPlan.DeferredTempDir", StringComparison.Ordinal)
            && job.Contains("cleanupPlan.DisposeError", StringComparison.Ordinal)
            && job.Contains("!preStartCleanupPending && !workerStillRunning", StringComparison.Ordinal)
            && cleanup.Contains("encoderOwnership.TakeDeferredCleanupCandidate()", StringComparison.Ordinal)
            && cleanup.Contains("EncoderPreStartOwnership<VideoEncoder>.FinishDeferredCleanup(", StringComparison.Ordinal)
            && cleanup.Contains("cleanupEncoders.ToArray()", StringComparison.Ordinal)
            && cleanup.Contains("deferredPreStart.Join(TimeSpan.FromSeconds(VideoEncoder.PreStartWaitSeconds + 35.0))", StringComparison.Ordinal);
        Console.WriteLine("    " + (ownershipWired ? "✓ " : "✗ ") + "RR09 预启动发布/收尾交接及有界候选清理已接线");
        if (!ownershipWired) fail++;
        bool resizeCleanupWired = job.Contains("if (!EncoderPreStartOwnership<VideoEncoder>.CanReplaceAfterDispose(", StringComparison.Ordinal)
            && job.Contains("encoder, current => current.Dispose(), current => current.HasActiveWorker))", StringComparison.Ordinal)
            && job.Contains("EncoderPreStartOwnership<VideoEncoder>.CreateAndBeginReplacement(", StringComparison.Ordinal)
            && job.Contains("value => encoder = value,", StringComparison.Ordinal)
            && job.Contains("value => value.Begin());", StringComparison.Ordinal)
            && job.Contains("encoder resize rebuild aborted: previous worker remains active", StringComparison.Ordinal);
        Console.WriteLine("    " + (resizeCleanupWired ? "✓ " : "✗ ") + "RR09 尺寸重建以旧泵空闲门控替换并在 Begin 前发布所有权");
        if (!resizeCleanupWired) fail++;
        bool deferredCleanupWired = job.Contains("cleanupPlan.DeferredEncoder", StringComparison.Ordinal)
            && job.Contains("cleanupPlan.DeferredTempDir", StringComparison.Ordinal)
            && job.Contains("cleanupPlan.DisposeError", StringComparison.Ordinal)
            && cleanup.Contains("if (deferredEncoder != null) cleanupEncoders.Add(deferredEncoder);", StringComparison.Ordinal)
            && cleanup.Contains("EncoderPreStartOwnership<VideoEncoder>.FinishDeferredCleanup(", StringComparison.Ordinal)
            && cleanup.Contains("cleanupEncoders.ToArray()", StringComparison.Ordinal)
            && ownershipSource.Contains("bool disposeFailed = false;", StringComparison.Ordinal)
            && ownershipSource.Contains("if (!disposeFailed && !active) break;", StringComparison.Ordinal)
            && ownershipSource.Contains("try { dispose(encoder); }", StringComparison.Ordinal)
            && ownershipSource.Contains("if (directoryExists(tempDir))\n                    deleteDirectory(tempDir);", StringComparison.Ordinal);
        Console.WriteLine("    " + (deferredCleanupWired ? "✓ " : "✗ ") + "RR09 Dispose 失败/活跃泵保留引用和目录，延迟清理最终释放并删除");
        if (!deferredCleanupWired) fail++;
        fail += RunBackpressureFixture();
        return fail;
    }

    private static string Real(string relativePath)
        => SrcUtil.Source(Driver.ProjectDir, relativePath);

    // Keep whole caller bodies, not a second implementation of their decisions. Only OS/Unity
    // boundaries are substituted. The capture setup is a contiguous top-level Phases slice;
    // selecting its outer resize if keeps the production replacement gate and failure path.
    private static string JobCallerSource(string source)
    {
        var job = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == "VideoRenderJob");
        MethodDeclarationSyntax Method(string name) => job.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == name);
        var phases = Method("Phases").Body.Statements;
        int first = phases.Select((statement, index) => (statement, index)).Single(pair =>
            pair.statement is ExpressionStatementSyntax expression
            && expression.Expression is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax access
            && access.Name.Identifier.ValueText == "ClaimPublished").index;
        int last = phases.Select((statement, index) => (statement, index)).Single(pair =>
            pair.statement is IfStatementSyntax branch
            && branch.Condition.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == "preW")).index;
        var boundaries = new JobBoundaryRewriter();
        string methods = string.Join("\n", new[]
        {
            "StartFinalizeOnWorker", "DisposePreStartCandidate", "TearDown", "DeferCleanupAfterWorker", "ReleasePendingFrame"
        }.Select(name => boundaries.Visit(Method(name)).ToFullString()));
        string setup = string.Join("\n", phases.Skip(first).Take(last - first + 1)
            .Select(statement => boundaries.Visit(statement).ToFullString()));
        return """
using System;
using System.Collections;
using System.Collections.Generic;
using SpectreReplay.Features.VideoRender;
public sealed partial class JobSubject
{
""" + methods + "\nprivate IEnumerator CaptureSetup() { var setupSw = System.Diagnostics.Stopwatch.StartNew();\n"
            + setup + "\nyield break; }\n}";
    }

    private sealed class JobBoundaryRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitQualifiedName(QualifiedNameSyntax node)
            => node.ToString() == "System.Threading.Thread"
                ? SyntaxFactory.IdentifierName("ControlledThread").WithTriviaFrom(node)
                : base.VisitQualifiedName(node);

        public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.ToString() == "System.Threading.Thread")
                return SyntaxFactory.IdentifierName("ControlledThread").WithTriviaFrom(node);
            if (node.ToString() == "DateTime.UtcNow")
                return SyntaxFactory.ParseExpression("FixtureClock.UtcNow").WithTriviaFrom(node);
            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            => node.Identifier.ValueText == "Directory"
                ? SyntaxFactory.IdentifierName("FixtureDirectory").WithTriviaFrom(node)
                : base.VisitIdentifierName(node);
    }

    private const string JobCallerFixture = """
using System;
using System.Collections.Generic;
using SpectreReplay.Features.VideoRender;
// A deterministic scheduler, not a claim that real Unity/OS threads were exercised.
// Start queues the actual production lambda; tests explicitly run it on a worker lane.
public sealed class ControlledThread
{
    private readonly Action body;
    private bool started, finished;
    private static readonly Queue<ControlledThread> pending = new();
    public static int Lane;
    private static int sleeps;
    public bool IsBackground { get; set; }
    public string Name { get; set; }
    public bool IsAlive => started && !finished;
    public ControlledThread(Action body) { this.body = body; }
    public void Start() { started = true; pending.Enqueue(this); }
    public bool Join(TimeSpan timeout) { Run(); return finished; }
    private void Run()
    {
        if (finished) return;
        int previous = Lane;
        Lane = 1;
        try { body(); }
        finally { finished = true; Lane = previous; }
    }
    public static void Drain() { while (pending.Count != 0) pending.Dequeue().Run(); }
    public static void Reset() { pending.Clear(); Lane = 0; sleeps = 0; FixtureClock.Reset(); }
    public static void Sleep(int milliseconds)
    {
        if (++sleeps > 4) throw new InvalidOperationException("controlled wait budget exhausted");
    }
}
public static class FixtureClock
{
    private static int reads;
    public static void Reset() { reads = 0; }
    public static DateTime UtcNow => new DateTime(2030, 1, 1).AddSeconds(31 * reads++);
}
public static class FixtureDirectory
{
    private static readonly HashSet<string> paths = new();
    public static readonly List<string> Deleted = new();
    public static void Reset() { paths.Clear(); Deleted.Clear(); }
    public static void Create(string path) { paths.Add(path); }
    public static bool Exists(string path) => path != null && paths.Contains(path);
    public static void Delete(string path, bool recursive)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        paths.Remove(path); Deleted.Add(path);
    }
}
public static class Debug
{
    public static void Log(object message) { }
    public static void LogWarning(object message) { }
    public static void LogError(object message) { }
    public static void LogException(Exception error) { }
}
public sealed class VideoEncoder
{
    public const double PreStartWaitSeconds = 5;
    public enum PreStartFailureReason { Generic, PreStartWaitTimeout }
    public enum Quality { Default }
    public enum VideoCodec { Default }
    public enum RateControlMode { Default }
    public sealed class PreStartException : Exception
    {
        public PreStartException(PreStartFailureReason reason, string text, Exception inner = null)
            : base(text, inner) { }
    }
    public static int Created;
    public bool Active;
    public int DisposeCount, BeginCount, ActiveReads, DisposeLane;
    public Func<int, bool> ReadActive;
    public Action OnDispose, FinishHook, MuxHook;
    public bool FinishInitiallyFalse, FinishLaterTrue, MuxInitiallyFalse, MuxLaterTrue;
    public int FinishCount, MuxCount;
    public string MuxPath;
    public double MuxOffset;
    public VideoEncoder(params object[] arguments) { Created++; }
    public bool HasActiveWorker
    {
        get { ActiveReads++; return ReadActive == null ? Active : ReadActive(ActiveReads); }
    }
    public void Dispose() { DisposeCount++; DisposeLane = ControlledThread.Lane; OnDispose?.Invoke(); }
    public void Begin() { BeginCount++; }
    public void FinishVideo(Func<bool> cancel = null)
    {
        FinishCount++;
        FinishInitiallyFalse = cancel != null && !cancel();
        FinishHook?.Invoke();
        FinishLaterTrue = cancel != null && cancel();
        if (FinishLaterTrue) throw new OperationCanceledException("finish canceled");
    }
    public void MuxWith(string wav, double offset, Func<bool> cancel = null)
    {
        MuxCount++; MuxPath = wav; MuxOffset = offset;
        MuxInitiallyFalse = cancel != null && !cancel();
        MuxHook?.Invoke();
        MuxLaterTrue = cancel != null && cancel();
        if (MuxLaterTrue) throw new OperationCanceledException("mux canceled");
    }
    public static PreStartFailureReason ClassifyPreStartFailure(Exception error) => PreStartFailureReason.Generic;
    public static string PreStartFailureLocalizationKey(PreStartFailureReason reason) => reason.ToString();
}
public sealed class VideoFrameSource
{
    public int Width = 2, Height = 2, BytesPerPixel = 4;
    public bool NeedsRowFlip;
    public int DisposeCount, DisposeLane = -1;
    public void Dispose() { DisposeCount++; DisposeLane = ControlledThread.Lane; }
}
public sealed class VideoAudioTrack { public void Dispose() { } }
public sealed class FixtureOptions
{
    public int Quality, QualityTier, Codec, RateControl, Crf, Cqp, Bitrate;
    public string Preset;
}
public static class SpectreState
{
    public static object data;
    public static class PlayActions
    {
        public static void SwitchToPlayMode() { }
        public static void SwitchToRecordMode() { }
    }
}
public static class SpectreAPI
{
    public enum RenderEndStatus { Failed }
    public static void RaiseRenderFinished(RenderEndStatus status, string output, string error) { }
}
public static class VideoRender
{
    public sealed class RenderRange { }
    public static bool CpuRetryPending;
    public static RenderRange CpuRetryRange;
    public static string CpuRetryReason;
    public static object CpuRetrySettings;
    public static void CloseJob() { }
    public static void StartCpuRetry(RenderRange range, string reason) { }
}
public static class LocalizationManager { public static string GetLocalizedText(string key) => key; }
namespace SpectreReplay.Ui { public static class Loc { public static string T(string key) => key; } }
public sealed partial class JobSubject
{
    private volatile bool cancelRequested, tornDown, finalizeDone;
    private volatile Exception finalizeError;
    private volatile bool finalizeErrorFromMux;
    private VideoEncoder encoder;
    private readonly EncoderPreStartOwnership<VideoEncoder> encoderOwnership = new();
    private VideoFrameSource frames = new();
    private VideoAudioTrack mixdown = new();
    private ControlledThread finalizeWorker, _encoderPreStart;
    private Exception _encoderPreStartError;
    private string tempDir = "job/controlled", wavPath = "job/controlled/mix.wav";
    private double muxAudioOffsetSec = -0.375;
    private bool finalizingReplay, consumeReplay, wasPlayModeBeforeRender;
    private object sourceReplay, gameObject;
    private SpectreAPI.RenderEndStatus endStatus;
    private string endOutput, endError;
    private int preW = 2, preH = 2, fps = 60, hardware, container;
    private bool preFlip, tenBit, hdr10;
    private string encoderPath = "controlled-encoder", rawVideoPath = "job/controlled/raw", outputPath = "out.mp4";
    private FixtureOptions options = new();
    private byte[] blackout;
    private byte[] pendingPixels;
    private Action<byte[]> pendingRecycle;
    private string failure;
    private void ReleaseGameControl() { }
    private void ReportEnd(SpectreAPI.RenderEndStatus status, string output, string error) { }
    private void Destroy(object target) { }
    private void LogTiming(string stage, string detail = null) { }
    private void NotifyCanceled() { failure = "canceled"; }
    private void NotifyFailed(string error) { failure = error; }
    private static JobSubject Fresh()
    {
        ControlledThread.Reset(); FixtureDirectory.Reset(); VideoEncoder.Created = 0;
        var job = new JobSubject(); FixtureDirectory.Create(job.tempDir); return job;
    }
    private static void Check(List<string> results, bool ok, string name)
        => results.Add((ok ? "✓ " : "✗ ") + name);
    private void Setup()
    {
        var setup = CaptureSetup();
        while (setup.MoveNext()) { }
    }
    public static string[] RunCases()
    {
        var r = new List<string>();
        var finishJob = Fresh();
        var finish = new VideoEncoder(); finishJob.encoder = finish;
        finish.FinishHook = () => finishJob.cancelRequested = true;
        finishJob.StartFinalizeOnWorker(); ControlledThread.Drain();
        Check(r, finish.FinishInitiallyFalse && finish.FinishLaterTrue
            && finishJob.finalizeDone && finishJob.finalizeError is OperationCanceledException
            && !finishJob.finalizeErrorFromMux && finish.MuxCount == 0,
            "RRB10 FinishVideoCaller_LiveCancellation: 真实收尾调用的取消委托在执行中由假变真");

        var muxJob = Fresh();
        var mux = new VideoEncoder(); muxJob.encoder = mux;
        string expectedWav = muxJob.wavPath; double expectedOffset = muxJob.muxAudioOffsetSec;
        mux.MuxHook = () => muxJob.cancelRequested = true;
        muxJob.StartFinalizeOnWorker();
        muxJob.wavPath = "changed.wav"; muxJob.muxAudioOffsetSec = 99;
        ControlledThread.Drain();
        Check(r, mux.FinishCount == 1 && mux.MuxInitiallyFalse && mux.MuxLaterTrue
            && mux.MuxPath == expectedWav && mux.MuxOffset == expectedOffset
            && muxJob.finalizeDone && muxJob.finalizeError is OperationCanceledException
            && muxJob.finalizeErrorFromMux,
            "RRB11 MuxCaller_CancellationAndOffset: 真实mux调用保留路径/偏移快照并观察后续取消");

        var frameJob = Fresh();
        var frameSource = frameJob.frames;
        frameJob.TearDown(); frameJob.TearDown(); ControlledThread.Drain();
        Check(r, frameSource.DisposeCount == 1 && frameSource.DisposeLane == 0 && frameJob.frames == null,
            "RRB12 TearDown_FramesDisposedOnMainLane: 帧源在调用主线程释放一次而非后台销毁");

        var claimJob = Fresh(); var published = new VideoEncoder();
        bool registered = claimJob.encoderOwnership.TryRegister(published);
        bool accepted = claimJob.encoderOwnership.TryPublish(published);
        claimJob.Setup();
        Check(r, registered && accepted && ReferenceEquals(claimJob.encoder, published) && claimJob.failure == null,
            "RRB17 CaptureSetup_ClaimsPublishedEncoder: 真实领取回调将已发布实例赋给作业字段");

        var candidateJob = Fresh(); var candidate = new VideoEncoder { Active = true };
        candidateJob.encoderOwnership.StartPreStart(() => { });
        bool candidateReturned = false;
        try { candidateJob.DisposePreStartCandidate(candidate); candidateReturned = true; }
        catch (InvalidOperationException) { } // A removed transfer must fail boundedly, never hang the suite.
        bool retained = candidateJob.encoderOwnership.HasDeferredCleanupCandidate;
        bool unavailableDuringBegin = candidateJob.encoderOwnership.TakeDeferredCleanupCandidate() == null;
        candidateJob.encoderOwnership.EndPreStart();
        var transferred = candidateJob.encoderOwnership.TakeDeferredCleanupCandidate();
        Check(r, candidateReturned && candidate.DisposeCount == 1 && retained && unavailableDuringBegin
            && ReferenceEquals(transferred, candidate),
            "RRB18 CandidateTimeout_RetainsForDeferredCleanup: 真实超时调用保留唯一候选且预启动退出前不可领取");

        var consumeJob = Fresh(); var pending = new VideoEncoder();
        consumeJob.encoderOwnership.RetainForDeferredCleanup(pending);
        consumeJob.DeferCleanupAfterWorker(null, consumeJob.tempDir, null);
        bool notDisposedBeforeSchedule = pending.DisposeCount == 0;
        ControlledThread.Drain();
        Check(r, notDisposedBeforeSchedule && pending.DisposeCount == 1 && pending.DisposeLane == 1
            && !consumeJob.encoderOwnership.HasDeferredCleanupCandidate && !FixtureDirectory.Exists(consumeJob.tempDir),
            "RRB19 DeferredCleanup_ConsumesCandidate: 真实后台清理领取并释放暂存候选后才删除目录");

        var snapshotJob = Fresh();
        var snapshot = new VideoEncoder { ReadActive = read => read == 1 }; snapshotJob.encoder = snapshot;
        snapshotJob.TearDown();
        bool singleSnapshot = snapshot.ActiveReads == 1 && snapshot.DisposeCount == 0
            && FixtureDirectory.Exists(snapshotJob.tempDir);
        ControlledThread.Drain();
        Check(r, singleSnapshot && snapshot.DisposeCount == 1 && !FixtureDirectory.Exists(snapshotJob.tempDir),
            "RRB23 TearDown_SingleWorkerSnapshot: 泵随后空闲也不能二次探测跳过延迟Dispose");

        var resizeJob = Fresh(); var old = new VideoEncoder { Active = true };
        resizeJob.encoder = old; resizeJob.frames.Width = 4;
        int creationsBeforeResize = VideoEncoder.Created; bool rebuildRejected = false;
        try { resizeJob.Setup(); }
        catch (VideoEncoder.PreStartException) { rebuildRejected = true; }
        Check(r, rebuildRejected && old.DisposeCount == 1 && ReferenceEquals(resizeJob.encoder, old)
            && VideoEncoder.Created == creationsBeforeResize && old.BeginCount == 0,
            "RRB25 CaptureResize_ActiveWorkerBlocksReplacement: 真实尺寸重建门拒绝替换仍活跃的旧编码器");
        var idleResizeJob = Fresh(); var idle = new VideoEncoder();
        idleResizeJob.encoder = idle; idleResizeJob.frames.Width = 4; idleResizeJob.Setup();
        Check(r, idle.DisposeCount == 1 && !ReferenceEquals(idleResizeJob.encoder, idle)
            && idleResizeJob.encoder.BeginCount == 1,
            "RRB25Control CaptureResize_IdleWorkerAllowsReplacement: 空闲对照实际创建并启动替代编码器");

        var deferredJob = Fresh(); var deferred = new VideoEncoder { Active = true };
        deferredJob.encoder = deferred;
        bool directoryPresentAtDispose = false;
        deferred.OnDispose = () =>
        {
            directoryPresentAtDispose = FixtureDirectory.Exists(deferredJob.tempDir);
            deferred.Active = false;
        };
        var deferredFrames = deferredJob.frames;
        deferredJob.TearDown();
        bool heldBeforeWorker = FixtureDirectory.Exists(deferredJob.tempDir) && deferred.DisposeCount == 0;
        ControlledThread.Drain();
        Check(r, heldBeforeWorker && deferredJob.encoder == null && deferred.DisposeCount == 1
            && deferred.DisposeLane == 1 && deferredFrames.DisposeCount == 1 && deferredFrames.DisposeLane == 0,
            "RRB26 TearDown_DeferredEncoderStrongHandoff: 清空作业字段后后台仍持有并释放原encoder");
        Check(r, heldBeforeWorker && directoryPresentAtDispose && !FixtureDirectory.Exists(deferredJob.tempDir)
            && FixtureDirectory.Deleted.Count == 1 && FixtureDirectory.Deleted[0] == deferredJob.tempDir,
            "RRB27 TearDown_DeferredTempDirectoryHandoff: 活跃泵目录保留到释放后再由后台删除同一路径");
        return r.ToArray();
    }
}
public static class RenderRepairFixture_JobCallers
{
    public static string[] Run() => JobSubject.RunCases();
}
""";

    private static string HasActiveWorkerSource(string encoderSource)
    {
        var root = CSharpSyntaxTree.ParseText(encoderSource).GetRoot();
        var property = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "HasActiveWorker");
        return """
using System;
using ManualResetEventSlim = System.Threading.ManualResetEventSlim;
public sealed class Thread
{
    public bool Alive;
    public bool Disposed;
    public bool BlockIsAlive;
    public readonly ManualResetEventSlim IsAliveEntered = new(false);
    public readonly ManualResetEventSlim AllowIsAlive = new(false);
    public bool IsAlive
    {
        get
        {
            if (BlockIsAlive)
            {
                IsAliveEntered.Set();
                if (!AllowIsAlive.Wait(2000)) throw new TimeoutException("IsAlive gate timed out");
            }
            if (Disposed) throw new ObjectDisposedException(nameof(Thread));
            return Alive;
        }
    }
}
public sealed class Process
{
    public bool Associated = true;
    public bool Disposed;
    public bool BlockHasExited;
    public readonly ManualResetEventSlim HasExitedEntered = new(false);
    public readonly ManualResetEventSlim AllowHasExited = new(false);
    public bool HasExited
    {
        get
        {
            if (BlockHasExited)
            {
                HasExitedEntered.Set();
                if (!AllowHasExited.Wait(2000)) throw new TimeoutException("HasExited gate timed out");
            }
            if (Disposed) throw new ObjectDisposedException(nameof(Process));
            if (!Associated) throw new InvalidOperationException("No process is associated");
            return true;
        }
    }
}
public sealed class Subject
{
    public volatile Thread pump;
    public volatile Process pipe;
""" + property.ToFullString() + "\n}";
    }

    private static int RunBackpressureFixture()
    {
        var pinType = CSharpSyntaxTree.ParseText(Real("Features/VideoRender/Patches/VideoRenderPatches.cs"))
            .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == "UnscaledDeltaPin");
        string PinMethod(string name) => pinType.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == name).ToString();
        var fields = pinType.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText is "armedFps"
                or "virtualElapsed" or "backpressureTimeScale" or "backpressurePaused"
                or "detours" or "dspDetour" or "realDspProbe"))
            .Select(f => f.ToString());
        var jobType = CSharpSyntaxTree.ParseText(Real("Features/VideoRender/Pipeline/VideoRenderJob.cs"))
            .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(t => t.Identifier.ValueText == "VideoRenderJob");
        var phases = jobType.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Phases");
        string waitMethod = jobType.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "WaitForBackpressure").ToString();
        // Extract the actual restore + capture-yield boundary. A missing restore must
        // stay compilable and fail behaviorally, not turn into a broken fixture.
        var captureLoop = phases.DescendantNodes().OfType<WhileStatementSyntax>()
            .Single(w => w.Condition.ToString().Contains("frameIndex < hardStop", StringComparison.Ordinal));
        var statements = ((BlockSyntax)captureLoop.Statement).Statements;
        int captureYield = statements.Select((s, i) => (s, i)).Single(p => p.s is YieldStatementSyntax y
            && y.Expression is ObjectCreationExpressionSyntax o && o.Type.ToString() == "WaitForEndOfFrame").i;
        string boundary = string.Join("\n", statements.Take(captureYield + 1)
            .Where(s => s is ExpressionStatementSyntax || s is YieldStatementSyntax).Select(s => s.ToFullString()));
        // Retain production readiness/cancellation loops and capture accounting, replacing
        // only the Unity/audio/encoder boundaries with controlled objects.
        var captureStep = statements.Where(s => s.ToString() is "mixdown.CaptureFrame();"
            or "frames.Grab(frameIndex);" or "frameIndex++;").ToArray();
        if (captureStep.Length != 3) throw new InvalidOperationException("capture step extraction changed");
        var captureFlush = statements.OfType<WhileStatementSyntax>()
            .Single(w => w.Condition.ToString() == "!Flush()");
        string captureFlow = captureLoop.WithStatement(SyntaxFactory.Block(
            statements.Take(captureYield + 1)
                .Append(SyntaxFactory.ParseStatement("UnscaledDeltaPin.TickFrame();"))
                .Concat(captureStep).Append(captureFlush))).ToString();
        string drainFlow = phases.Body.Statements.OfType<WhileStatementSyntax>()
            .Single(w => w.Condition.ToString().StartsWith("frames.InFlight > 0", StringComparison.Ordinal)).ToString();
        int waits = phases.DescendantNodes().OfType<YieldStatementSyntax>()
            .Count(y => y.Expression?.ToString() == "WaitForBackpressure()");
        string deltaProperty = pinType.Members.OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "CurrentDelta").ToString();
        string fixture = $$"""
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SpectreReplay.Features.VideoRender;
namespace SpectreReplay.Features.VideoRender
{
    public static class UnscaledDeltaPin
    {
        {{string.Join("\n", fields)}}
        {{PinMethod("BeginBackpressureWait")}}
        {{PinMethod("EndBackpressureWait")}}
        {{PinMethod("TickFrame")}}
        {{PinMethod("PinnedDelta")}}
        {{PinMethod("Uninstall")}}
        {{deltaProperty}}
        public static float Delta => PinnedDelta();
        public static bool Paused => backpressurePaused;
        public static int Fps { set => armedFps = value; }
        public static double Elapsed { get => virtualElapsed; set => virtualElapsed = value; }
    }
    public sealed class JobSubject
    {
        {{waitMethod}}
        public object Wait() => WaitForBackpressure();
        public IEnumerator PrepareCapture() { {{boundary}} }
        public sealed class FrameBoundary
        {
            public int InFlight, MaxInFlight = 2, Captures;
            public void Grab(int index) { Captures++; InFlight++; }
        }
        public sealed class AudioBoundary
        {
            public int Captures;
            public void CaptureFrame() { Captures++; }
        }
        public readonly FrameBoundary frames = new();
        public readonly AudioBoundary mixdown = new();
        public bool cancelRequested, sceneExited, GpuReady;
        public bool QueueReady = true;
        private int frameIndex, hardStop = 2;
        private bool Flush()
        {
            if (cancelRequested) throw new OperationCanceledException();
            if (!QueueReady) return false;
            if (GpuReady) frames.InFlight = 0;
            return true;
        }
        public IEnumerator Capture() { {{captureFlow}} }
        public IEnumerator Drain()
        {
            var drainClock = System.Diagnostics.Stopwatch.StartNew();
            {{drainFlow}}
        }
    }
}
namespace UnityEngine
{
    public sealed class WaitForEndOfFrame { }
    public static class Time { public static float timeScale = 1f; }
    public static class Debug
    {
        public static void Log(object o) { }
        public static void LogError(object o) { }
    }
}
public static class RenderRepairFixture_BackpressurePause
{
    private static void Check(List<string> r, bool ok, string name) => r.Add((ok ? "✓ " : "✗ ") + name);
    private static bool Advance(IEnumerator flow, ref int updates, ref bool valid)
    {
        if (!flow.MoveNext()) return false;
        valid &= flow.Current is WaitForEndOfFrame;
        if (UnscaledDeltaPin.Paused)
            valid &= UnscaledDeltaPin.Delta == 0f && Time.timeScale == 0f;
        else
        {
            updates++;
            valid &= Math.Abs(UnscaledDeltaPin.Delta - 0.02f) < 1e-6;
        }
        return true;
    }
    public static string[] Run()
    {
        var r = new List<string>(); var job = new JobSubject();
        UnscaledDeltaPin.Fps = 50; UnscaledDeltaPin.Elapsed = 2.0; Time.timeScale = 0.75f;
        bool frozen = true; double managedStep = 0, nativeStep = 0, before = UnscaledDeltaPin.Elapsed;
        for (int poll = 0; poll < 3; poll++)
        {
            object wait = job.Wait();
            frozen &= wait is WaitForEndOfFrame && Time.timeScale == 0f && UnscaledDeltaPin.Paused;
            // A deterministic frame-order model, not a claim about Unity native execution.
            nativeStep += 0.02 * Time.timeScale; managedStep += UnscaledDeltaPin.Delta;
            frozen &= UnscaledDeltaPin.CurrentDelta == 0f; UnscaledDeltaPin.TickFrame();
        }
        Check(r, frozen && nativeStep == 0 && managedStep == 0 && UnscaledDeltaPin.Elapsed == before,
            "three EOF stall polls freeze scaled and both managed delta paths without ticking");
        var capture = job.PrepareCapture();
        bool prepared = capture.MoveNext() && capture.Current is WaitForEndOfFrame;
        bool restoredBeforeUpdate = !UnscaledDeltaPin.Paused && Time.timeScale == 0.75f;
        nativeStep += 0.02 * Time.timeScale; managedStep += UnscaledDeltaPin.Delta;
        bool complete = !capture.MoveNext(); UnscaledDeltaPin.TickFrame();
        Check(r, prepared && restoredBeforeUpdate && complete && Math.Abs(nativeStep - 0.015) < 1e-9
            && Math.Abs(managedStep - 0.02) < 1e-6 && Math.Abs(UnscaledDeltaPin.Elapsed - (before + 0.02)) < 1e-12,
            "queue recovery restores at prior EOF before the next Update and advances one capture step");
        Time.timeScale = 0.4f; job.Wait(); job.Wait();
        UnscaledDeltaPin.Uninstall(); UnscaledDeltaPin.Uninstall();
        Check(r, !UnscaledDeltaPin.Paused && Time.timeScale == 0.4f,
            "cancel/destroy clock uninstall restores the original scale once after repeated GPU waits");
        Check(r, {{waits}} == 5, "three encoder and both GPU stall yields use the same EOF pause boundary");
        for (int mode = 0; mode < 3; mode++)
        {
            UnscaledDeltaPin.Uninstall(); UnscaledDeltaPin.Elapsed = 0; Time.timeScale = 0.75f;
            var subject = new JobSubject();
            if (mode == 1) subject.QueueReady = false;
            if (mode == 2) subject.frames.InFlight = subject.frames.MaxInFlight;
            var flow = subject.Capture(); int updates = 0; bool valid = true;
            valid = Advance(flow, ref updates, ref valid) && valid;
            if (mode == 1) valid = Advance(flow, ref updates, ref valid) && valid;
            if (mode != 0)
            {
                int captured = subject.frames.Captures;
                double elapsed = UnscaledDeltaPin.Elapsed;
                for (int poll = 0; poll < 3; poll++) valid = Advance(flow, ref updates, ref valid) && valid;
                valid &= subject.frames.Captures == captured && UnscaledDeltaPin.Elapsed == elapsed;
            }
            subject.QueueReady = true; subject.GpuReady = true;
            int remaining = 8;
            while (remaining-- > 0 && Advance(flow, ref updates, ref valid)) { }
            Check(r, valid && remaining > 0 && updates == 2 && subject.frames.Captures == 2
                && subject.mixdown.Captures == 2 && Math.Abs(UnscaledDeltaPin.Elapsed - 0.04) < 1e-12
                && !UnscaledDeltaPin.Paused && Time.timeScale == 0.75f,
                "production capture loop has exactly two updates/audio/video frames: "
                    + (mode == 0 ? "no stall" : mode == 1 ? "encoder queue stall" : "GPU-only stall"));
        }
        UnscaledDeltaPin.Uninstall(); Time.timeScale = 0.4f;
        var draining = new JobSubject(); draining.frames.InFlight = 2;
        var drain = draining.Drain(); int drainUpdates = 0; bool drainValid = true;
        for (int poll = 0; poll < 3; poll++) drainValid = Advance(drain, ref drainUpdates, ref drainValid) && drainValid;
        draining.GpuReady = true;
        drainValid &= !drain.MoveNext() && UnscaledDeltaPin.Paused && draining.frames.Captures == 0;
        UnscaledDeltaPin.Uninstall();
        Check(r, drainValid && drainUpdates == 0 && Time.timeScale == 0.4f,
            "production final GPU drain stays frozen through recovery until control release");
        var canceling = new JobSubject(); canceling.QueueReady = false;
        var cancelFlow = canceling.Capture(); int cancelUpdates = 0; bool cancelValid = true;
        cancelValid = Advance(cancelFlow, ref cancelUpdates, ref cancelValid) && cancelValid;
        cancelValid = Advance(cancelFlow, ref cancelUpdates, ref cancelValid) && cancelValid;
        canceling.cancelRequested = true; bool canceled = false;
        try { cancelFlow.MoveNext(); } catch (OperationCanceledException) { canceled = true; }
        UnscaledDeltaPin.Uninstall();
        Check(r, cancelValid && canceled && cancelUpdates == 1 && canceling.frames.Captures == 1
            && !UnscaledDeltaPin.Paused && Time.timeScale == 0.4f,
            "production encoder wait observes cancellation without an extra capture and restores scale");
        return r.ToArray();
    }
}
""";
        return RunFixture("BackpressurePause", fixture);
    }

    private static int RunFixture(string name, params string[] sources)
    {
        try
        {
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                .Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location,
                    StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("RenderRepair_" + name + "_" + Guid.NewGuid().ToString("N"),
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), trusted,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)));
            Type fixture = Assembly.Load(image.ToArray()).GetType("RenderRepairFixture_" + name);
            var run = fixture.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            var results = (string[])run.Invoke(null, null);
            int failed = results.Count(r => r.StartsWith("✗", StringComparison.Ordinal));
            foreach (string result in results) Console.WriteLine("    " + result);
            return failed;
        }
        catch (Exception ex)
        {
            Console.WriteLine("    ✗ " + name + " fixture failed: " + (ex.InnerException ?? ex).Message);
            return 1;
        }
    }

    private const string HasActiveWorkerFixture = """
using System;
using System.Collections.Generic;
public static class RenderRepairFixture_HasActiveWorker
{
    private static void Check(List<string> r, bool ok, string label)
        => r.Add((ok ? "✓ " : "✗ ") + label);

    private static bool Read(Subject subject, out Exception error)
    {
        error = null;
        try { return subject.HasActiveWorker; }
        catch (Exception ex) { error = ex; return false; }
    }

    public static string[] Run()
    {
        var r = new List<string>();

        var pumpSubject = new Subject();
        var pump = new Thread { Alive = true, BlockIsAlive = true };
        pumpSubject.pump = pump;
        var releasePump = new System.Threading.Thread(() =>
        {
            if (!pump.IsAliveEntered.Wait(2000)) return;
            pumpSubject.pump = null;
            pump.Disposed = true;
            pump.AllowIsAlive.Set();
        });
        releasePump.Start();
        bool pumpResult = Read(pumpSubject, out Exception pumpError);
        bool pumpJoined = releasePump.Join(2000);
        Check(r, pumpJoined && pumpError == null && pumpResult,
            "读取 IsAlive 期间 pump 被置空/释放时不抛异常，并保守保留临时目录");

        var pipeSubject = new Subject();
        var process = new Process { BlockHasExited = true };
        pipeSubject.pipe = process;
        var releasePipe = new System.Threading.Thread(() =>
        {
            if (!process.HasExitedEntered.Wait(2000)) return;
            pipeSubject.pipe = null;
            process.Disposed = true;
            process.AllowHasExited.Set();
        });
        releasePipe.Start();
        bool pipeResult = Read(pipeSubject, out Exception pipeError);
        bool pipeJoined = releasePipe.Join(2000);
        Check(r, pipeJoined && pipeError == null && !pipeResult,
            "读取 HasExited 期间 pipe 被置空/释放时安全按无关联进程处理");

        var unassociated = new Subject { pipe = new Process { Associated = false } };
        bool noProcessResult = Read(unassociated, out Exception noProcessError);
        Check(r, noProcessError == null && !noProcessResult,
            "无关联 Process 读取安全返回非活跃，不把异常泄漏给延迟清理");

        return r.ToArray();
    }
}
""";

    private const string FrameBufferFixture = """
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
public static class RenderRepairFixture_FrameBufferOwnership
{
    private static void Check(List<string> r, bool ok, string label)
        => r.Add((ok ? "✓ " : "✗ ") + label);
    public static string[] Run()
    {
        var r = new List<string>();
        var owner = new SpectreReplay.Features.VideoRender.FrameBufferOwnership(4);
        byte[] first = owner.RentForFrame(8);
        for (int i = 0; i < first.Length; i++) first[i] = (byte)(i + 1);
        owner.RecycleOrHold(first);
        var sourceReady = new ManualResetEventSlim(false);
        var allowCopy = new ManualResetEventSlim(false);
        var copyDone = new ManualResetEventSlim(false);
        byte[] source = null;
        byte[] copy = null;
        Exception error = null;
        var copier = new Thread(() =>
        {
            try
            {
                byte[] destination;
                if (!owner.TryRentSubstitute(8, out source, out destination))
                    throw new InvalidOperationException("substitute source missing");
                copy = destination;
                sourceReady.Set();
                allowCopy.Wait();
                Buffer.BlockCopy(source, 0, copy, 0, 8);
                owner.ReleaseSubstituteSource(source);
                copyDone.Set();
            }
            catch (Exception ex) { error = ex; copyDone.Set(); }
        });
        copier.Start();
        bool pinEstablished = sourceReady.Wait(2000);
        byte[] next = owner.RentForFrame(8);
        bool noEarlyReuse = !ReferenceEquals(first, next);
        allowCopy.Set();
        bool joined = copier.Join(2000);
        bool copied = joined && error == null && copyDone.IsSet && copy.SequenceEqual(first);
        owner.RecycleOrHold(next);
        byte[] reusedAfterRelease = owner.RentForFrame(8);
        bool releasedForReuse = ReferenceEquals(first, reusedAfterRelease);
        owner.Dispose();
        bool lateCallbackSafe = true;
        try { owner.RecycleOrHold(reusedAfterRelease); } catch { lateCallbackSafe = false; }
        Check(r, pinEstablished && noEarlyReuse, "复制期间源 buffer 被 pin，退役不会提前回池复用");
        Check(r, copied && releasedForReuse, "锁外复制完成后释放 pin，源内容完整且可再次复用");
        var spareField = owner.GetType().GetField("spares", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        lateCallbackSafe &= ((System.Collections.ICollection)spareField.GetValue(owner)).Count == 0;
        Check(r, lateCallbackSafe, "Dispose 后晚到回调安全丢弃，不重新写入已关闭池");
        var race = new SpectreReplay.Features.VideoRender.FrameBufferOwnership(4);
        byte[] a = race.RentForFrame(8);
        var gate = race.GetType().GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(race);
        var entered = new ManualResetEventSlim(false);
        var returned = new ManualResetEventSlim(false);
        var callback = new Thread(() => { entered.Set(); race.RecycleOrHold(a); returned.Set(); });
        bool serialized; byte[] b;
        lock (gate)
        {
            callback.Start();
            bool ready = entered.Wait(2000);
            serialized = ready && !returned.Wait(100);
            b = race.RentForFrame(8);
        }
        bool callbackJoined = callback.Join(2000);
        byte[] c = race.RentForFrame(8);
        Check(r, serialized && callbackJoined && !ReferenceEquals(b, c), "RR09 A迟到回调与B登记被同锁串行化，B编码前绝不回池");
        race.Dispose();
        return r.ToArray();
    }
}
""";

    private const string EncoderOwnershipFixture = """
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SpectreReplay.Features.VideoRender;
public sealed class Candidate : IDisposable
{
    public int DisposeCount;
    public bool Active;
    public void Dispose() { DisposeCount++; }
}
public static class RenderRepairFixture_EncoderOwnership
{
    private static void Check(List<string> r, bool ok, string label)
        => r.Add((ok ? "✓ " : "✗ ") + label);
    public static string[] Run()
    {
        var r = new List<string>();
        var destroyFirst = new EncoderPreStartOwnership<Candidate>();
        var pending = new Candidate();
        var beginEntered = new ManualResetEventSlim(false);
        var allowBeginReturn = new ManualResetEventSlim(false);
        var deferredStored = new ManualResetEventSlim(false);
        var allowThreadExit = new ManualResetEventSlim(false);
        bool registered = false;
        bool publishedAfterClose = true;
        Candidate released = null;
        bool retained = false;
        var beginThread = new Thread(() =>
        {
            try
            {
                registered = destroyFirst.TryRegister(pending);
                beginEntered.Set();
                allowBeginReturn.Wait();
                publishedAfterClose = destroyFirst.TryPublish(pending);
                released = destroyFirst.Abandon(pending);
                retained = released != null && destroyFirst.RetainForDeferredCleanup(released);
                deferredStored.Set();
                allowThreadExit.Wait();
            }
            finally { destroyFirst.EndPreStart(); }
        });
        destroyFirst.StartPreStart(() => beginThread.Start());
        bool reachedBegin = beginEntered.Wait(2000);
        bool closeWon = destroyFirst.Close(null, out Candidate closing, out bool preStartActive);
        string tempDir = Path.Combine(Path.GetTempPath(), "SpectreReplay-EncoderOwner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        bool prematureDeleteAllowed = EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(false, true, true);
        if (prematureDeleteAllowed) Directory.Delete(tempDir, true);
        bool directoryRetained = Directory.Exists(tempDir);
        bool otherWorkersBlock = !EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(true, false, true)
            && !EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(true, true, false);
        allowBeginReturn.Set();
        bool transferFinished = deferredStored.Wait(2000);
        bool takeBeforeExitBlocked = destroyFirst.TakeDeferredCleanupCandidate() == null
            && destroyFirst.HasDeferredCleanupCandidate;
        allowThreadExit.Set();
        bool beginExited = beginThread.Join(3000);
        Candidate cleanupCandidate = destroyFirst.TakeDeferredCleanupCandidate();
        if (cleanupCandidate != null) cleanupCandidate.Dispose();
        bool singleOwnerTransfer = retained && ReferenceEquals(released, pending)
            && ReferenceEquals(cleanupCandidate, pending) && pending.DisposeCount == 1;
        Check(r, reachedBegin && registered && closeWon && preStartActive && closing == null
            && !publishedAfterClose && transferFinished && takeBeforeExitBlocked && beginExited && singleOwnerTransfer,
            "销毁抢先：Begin candidate 被闭闸拒绝发布，退出后才转交并释放一次");
        Check(r, !prematureDeleteAllowed && directoryRetained && otherWorkersBlock,
            "预启动未退出、收尾线程存活或编码泵活跃时均禁止删除目录");
        bool deleteAfterExitAllowed = EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(beginExited, true, true);
        if (deleteAfterExitAllowed && Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        bool directoryRemovedAfterExit = !Directory.Exists(tempDir);
        Check(r, deleteAfterExitAllowed && directoryRemovedAfterExit,
            "预启动退出且收尾完成、编码泵空闲后允许删除临时目录");
        if (pending.DisposeCount == 0) pending.Dispose();
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);

        var publishFirst = new EncoderPreStartOwnership<Candidate>();
        var accepted = new Candidate();
        var registeredSignal = new ManualResetEventSlim(false);
        var allowPublish = new ManualResetEventSlim(false);
        var publishedSignal = new ManualResetEventSlim(false);
        var allowPublishedThreadExit = new ManualResetEventSlim(false);
        bool acceptedRegistered = false;
        bool acceptedPublished = false;
        var publisher = new Thread(() =>
        {
            try
            {
                acceptedRegistered = publishFirst.TryRegister(accepted);
                registeredSignal.Set();
                allowPublish.Wait();
                acceptedPublished = publishFirst.TryPublish(accepted);
                publishedSignal.Set();
                allowPublishedThreadExit.Wait();
            }
            finally { publishFirst.EndPreStart(); }
        });
        publishFirst.StartPreStart(() => publisher.Start());
        bool publisherReady = registeredSignal.Wait(2000);
        allowPublish.Set();
        bool publicationVisible = publishedSignal.Wait(2000);
        bool teardownTookPublished = publishFirst.Close(null, out Candidate teardownClaimed,
            out bool publisherStillActive);
        allowPublishedThreadExit.Set();
        bool publisherExited = publisher.Join(3000);
        if (teardownClaimed != null) teardownClaimed.Dispose();
        Check(r, publisherReady && acceptedRegistered && publicationVisible && acceptedPublished
            && teardownTookPublished && publisherStillActive && publisherExited
            && ReferenceEquals(teardownClaimed, accepted) && accepted.DisposeCount == 1,
            "发布抢先且尚未领取时，销毁从闩锁取得唯一 encoder 所有权");

        var claimedFirst = new EncoderPreStartOwnership<Candidate>();
        var claimedCandidate = new Candidate();
        var claimRegistered = new ManualResetEventSlim(false);
        var allowClaimPublish = new ManualResetEventSlim(false);
        var claimPublishedSignal = new ManualResetEventSlim(false);
        var allowClaimThreadExit = new ManualResetEventSlim(false);
        bool claimWasRegistered = false;
        bool claimWasPublished = false;
        var claimThread = new Thread(() =>
        {
            try
            {
                claimWasRegistered = claimedFirst.TryRegister(claimedCandidate);
                claimRegistered.Set();
                allowClaimPublish.Wait();
                claimWasPublished = claimedFirst.TryPublish(claimedCandidate);
                claimPublishedSignal.Set();
                allowClaimThreadExit.Wait();
            }
            finally { claimedFirst.EndPreStart(); }
        });
        claimedFirst.StartPreStart(() => claimThread.Start());
        bool claimReady = claimRegistered.Wait(2000);
        allowClaimPublish.Set();
        bool claimPublishedReady = claimPublishedSignal.Wait(2000);
        Candidate mainThreadClaim = null;
        claimedFirst.ClaimPublished(value => mainThreadClaim = value);
        bool teardownAfterClaim = claimedFirst.Close(mainThreadClaim, out Candidate claimedForClose,
            out bool claimPreStartActive);
        allowClaimThreadExit.Set();
        bool claimThreadExited = claimThread.Join(3000);
        if (claimedForClose != null) claimedForClose.Dispose();
        Check(r, claimReady && claimWasRegistered && claimPublishedReady && claimWasPublished
            && ReferenceEquals(mainThreadClaim, claimedCandidate) && teardownAfterClaim
            && claimPreStartActive && claimThreadExited && ReferenceEquals(claimedForClose, claimedCandidate)
            && claimedCandidate.DisposeCount == 1,
            "主线程先领取后销毁仍从同一闩锁接管 encoder");

        int activeState = 1;
        var stopPump = new ManualResetEventSlim(false);
        var pumpThread = new Thread(() =>
        {
            stopPump.Wait();
            Volatile.Write(ref activeState, 0);
        });
        pumpThread.Start();
        bool activeAtSnapshot = Volatile.Read(ref activeState) != 0;
        stopPump.Set();
        bool pumpExited = pumpThread.Join(2000);
        bool idleOnLaterProbe = Volatile.Read(ref activeState) == 0;
        bool deferFromSnapshot = EncoderPreStartOwnership<Candidate>.MustDeferCleanup(
            false, false, false, activeAtSnapshot);
        bool secondProbeWouldDelete = EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(
            true, true, idleOnLaterProbe);
        bool stableSnapshotKeepsDirectory = !EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(
            true, true, !deferFromSnapshot);
        Check(r, pumpExited && activeAtSnapshot && idleOnLaterProbe && deferFromSnapshot
            && secondProbeWouldDelete && stableSnapshotKeepsDirectory,
            "活跃快照后泵退出仍延迟 Dispose，不能用第二次探测跳过资源释放");

        var activeCandidate = new Candidate { Active = true };
        bool activeReplacementAllowed = EncoderPreStartOwnership<Candidate>.CanReplaceAfterDispose(
            activeCandidate, value => value.Dispose(), value => value.Active);
        var idleCandidate = new Candidate { Active = false };
        bool idleReplacementAllowed = EncoderPreStartOwnership<Candidate>.CanReplaceAfterDispose(
            idleCandidate, value => value.Dispose(), value => value.Active);
        Check(r, !activeReplacementAllowed && activeCandidate.DisposeCount == 1
            && idleReplacementAllowed && idleCandidate.DisposeCount == 1,
            "尺寸重建在旧编码泵仍活跃时保留所有权，空闲后才允许创建替代编码器");
        string cleanupDir = "resize-cleanup-temp";
        var throwingCandidate = new Candidate();
        var throwingPlan = EncoderPreStartOwnership<Candidate>.CreateCleanupPlan(
            throwingCandidate, cleanupDir, false,
            value => throw new InvalidOperationException("dispose failed"), value => value.Active);
        bool disposeFailureRetained = throwingPlan.MustDefer
            && ReferenceEquals(throwingPlan.DeferredEncoder, throwingCandidate)
            && throwingPlan.DeferredTempDir == cleanupDir
            && throwingPlan.DisposeError is InvalidOperationException
            && !EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(true, true, !throwingPlan.MustDefer);
        Check(r, disposeFailureRetained,
            "Dispose 异常交接旧 encoder 与临时目录给延迟清理并禁止立即删除");

        var activeCleanupCandidate = new Candidate { Active = true };
        var activePlan = EncoderPreStartOwnership<Candidate>.CreateCleanupPlan(
            activeCleanupCandidate, cleanupDir, false, value => value.Dispose(), value => value.Active);
        bool activePumpRetained = activePlan.MustDefer
            && ReferenceEquals(activePlan.DeferredEncoder, activeCleanupCandidate)
            && activePlan.DeferredTempDir == cleanupDir;
        Check(r, activePumpRetained,
            "Dispose 返回但旧泵仍活跃时交接 encoder 与临时目录并禁止丢失引用");

        var idleCleanupCandidate = new Candidate { Active = false };
        var idlePlan = EncoderPreStartOwnership<Candidate>.CreateCleanupPlan(
            idleCleanupCandidate, cleanupDir, false, value => value.Dispose(), value => value.Active);
        bool idleReleased = !idlePlan.MustDefer && idlePlan.DeferredEncoder == null
            && idlePlan.DeferredTempDir == null && idleCleanupCandidate.DisposeCount == 1
            && EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(true, true, !idlePlan.MustDefer);
        Check(r, idleReleased, "旧泵已空闲且 Dispose 成功时不重复交接，可正常删除临时目录");

        var asyncOnlyPlan = EncoderPreStartOwnership<Candidate>.CreateCleanupPlan(
            null, cleanupDir, true, value => value.Dispose(), value => value.Active);
        Check(r, asyncOnlyPlan.MustDefer && asyncOnlyPlan.DeferredEncoder == null
            && asyncOnlyPlan.DeferredTempDir == cleanupDir,
            "仅收尾线程存活但没有 encoder 时仍将临时目录交给延迟清理");

        var replacementCandidate = new Candidate { Active = true };
        Candidate replacementOwner = null;
        bool ownerVisibleAtBegin = false;
        bool beginFailed = false;
        try
        {
            EncoderPreStartOwnership<Candidate>.CreateAndBeginReplacement(
                () => replacementCandidate,
                value => replacementOwner = value,
                value =>
                {
                    ownerVisibleAtBegin = ReferenceEquals(replacementOwner, value);
                    throw new InvalidOperationException("begin failed");
                });
        }
        catch (InvalidOperationException) { beginFailed = true; }
        var failedBeginPlan = EncoderPreStartOwnership<Candidate>.CreateCleanupPlan(
            replacementOwner, cleanupDir, false, value => value.Dispose(), value => value.Active);
        Check(r, beginFailed && ownerVisibleAtBegin && ReferenceEquals(replacementOwner, replacementCandidate)
            && failedBeginPlan.MustDefer && ReferenceEquals(failedBeginPlan.DeferredEncoder, replacementCandidate)
            && failedBeginPlan.DeferredTempDir == cleanupDir,
            "替代 encoder 在 Begin 前登记，Begin 失败仍能由收场接管引用和目录");

        Candidate constructorOwner = null;
        bool constructorFailed = false;
        try
        {
            EncoderPreStartOwnership<Candidate>.CreateAndBeginReplacement(
                () => throw new InvalidOperationException("constructor failed"),
                value => constructorOwner = value, value => { });
        }
        catch (InvalidOperationException) { constructorFailed = true; }
        Check(r, constructorFailed && constructorOwner == null,
            "替代 encoder 构造失败时不发布半成品引用，旧实例已在此前释放");

        string deferredDirectory = Path.Combine(Path.GetTempPath(), "SpectreReplay-DeferredCleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(deferredDirectory);
        var directoryCandidate = new Candidate { Active = true };
        var directoryPlan = EncoderPreStartOwnership<Candidate>.CreateCleanupPlan(
            directoryCandidate, deferredDirectory, false, value => value.Dispose(), value => value.Active);
        bool blockedDelete = directoryPlan.MustDefer
            && !EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(true, true, !directoryPlan.MustDefer);
        if (!blockedDelete) Directory.Delete(deferredDirectory, true);
        bool deferredDirectoryRetained = Directory.Exists(deferredDirectory)
            && ReferenceEquals(directoryPlan.DeferredEncoder, directoryCandidate)
            && directoryPlan.DeferredTempDir == deferredDirectory;
        int cleanupTimeoutChecks = 0;
        bool delayedCleanupFinished = EncoderPreStartOwnership<Candidate>.FinishDeferredCleanup(
            new[] { directoryCandidate }, deferredDirectory,
            value => { value.Dispose(); value.Active = false; },
            value => value.Active,
            Directory.Exists,
            path => Directory.Delete(path, true),
            () => ++cleanupTimeoutChecks >= 3,
            () => { },
            ex => { });
        bool delayedDeleteAllowed = EncoderPreStartOwnership<Candidate>.CanDeleteTemporaryDirectory(
            true, true, !directoryCandidate.Active);
        Check(r, blockedDelete && deferredDirectoryRetained && delayedCleanupFinished
            && delayedDeleteAllowed && !Directory.Exists(deferredDirectory)
            && directoryCandidate.DisposeCount == 2,
            "延迟清理持有资源与目录，泵退出后再次 Dispose 并删除目录");
        if (Directory.Exists(deferredDirectory)) Directory.Delete(deferredDirectory, true);

        string retryDirectory = Path.Combine(Path.GetTempPath(), "SpectreReplay-DisposeRetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retryDirectory);
        var retryCandidate = new Candidate { Active = false };
        int retryAttempts = 0;
        var retryPlanFinished = EncoderPreStartOwnership<Candidate>.FinishDeferredCleanup(
            new[] { retryCandidate }, retryDirectory,
            value =>
            {
                retryAttempts++;
                if (retryAttempts == 1) throw new InvalidOperationException("transient dispose failure");
                value.Dispose();
            },
            value => value.Active,
            Directory.Exists,
            path => Directory.Delete(path, true),
            () => false,
            () => { },
            ex => { });
        Check(r, retryPlanFinished && retryAttempts == 2 && !Directory.Exists(retryDirectory),
            "Dispose 瞬时失败时先重试成功，再删除延迟目录");
        if (Directory.Exists(retryDirectory)) Directory.Delete(retryDirectory, true);

        string failedDirectory = Path.Combine(Path.GetTempPath(), "SpectreReplay-DisposeFailed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(failedDirectory);
        int failedAttempts = 0;
        bool failedCleanupFinished = EncoderPreStartOwnership<Candidate>.FinishDeferredCleanup(
            new[] { new Candidate() }, failedDirectory,
            value => { failedAttempts++; throw new InvalidOperationException("permanent dispose failure"); },
            value => false,
            Directory.Exists,
            path => Directory.Delete(path, true),
            () => failedAttempts >= 2,
            () => { },
            ex => { });
        Check(r, !failedCleanupFinished && failedAttempts == 2 && Directory.Exists(failedDirectory),
            "Dispose 持续失败时保留 encoder 所属临时目录，不提前删除");
        if (Directory.Exists(failedDirectory)) Directory.Delete(failedDirectory, true);
        return r.ToArray();
    }
}
""";
    private const string CancellationFixture = """
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
public static class RenderRepairFixture_Cancellation
{
    private static void Check(List<string> r, bool ok, string label)
        => r.Add((ok ? "✓ " : "✗ ") + label);
    public static string[] Run()
    {
        var r = new List<string>();
        int canceled = 0;
        var release = new ManualResetEventSlim(false);
        var cancelThread = new Thread(() => { Thread.Sleep(80); Volatile.Write(ref canceled, 1); });
        cancelThread.Start();
        bool drainCanceled = false;
        try
        {
            drainCanceled = SpectreReplay.Features.VideoRender.CancellableWait.Wait(
                ms => release.Wait(ms), () => Volatile.Read(ref canceled) != 0,
                TimeSpan.FromSeconds(2), 20)
                == SpectreReplay.Features.VideoRender.CancellableWaitResult.Canceled;
        }
        catch (TimeoutException) { drainCanceled = false; }
        finally { cancelThread.Join(); }
        Check(r, drainCanceled, "后台排空轮询在取消后有界返回 Canceled");

        int processCanceled = 0;
        int childId = 0;
        var childStarted = new ManualResetEventSlim(false);
        var processCancel = new Thread(() =>
        {
            if (childStarted.Wait(2000))
            {
                Thread.Sleep(80);
                Volatile.Write(ref processCanceled, 1);
            }
        });
        processCancel.Start();
        bool gotCanceled = false;
        bool childExited = false;
        try
        {
            SpectreReplay.Features.VideoRender.CancellableProcessRunner.RunAndWait(
                "pwsh.exe",
                "-NoLogo -NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 30\"",
                () => Volatile.Read(ref processCanceled) != 0,
                TimeSpan.FromSeconds(3), false, id => { childId = id; childStarted.Set(); });
        }
        catch (OperationCanceledException) { gotCanceled = true; }
        catch (TimeoutException) { gotCanceled = false; }
        finally { processCancel.Join(); }
        try { childExited = Process.GetProcessById(childId).HasExited; }
        catch (ArgumentException) { childExited = childId != 0; }
        if (!childExited && childId != 0)
        {
            try { using var leftover = Process.GetProcessById(childId); leftover.Kill(); leftover.WaitForExit(3000); } catch { }
        }
        Check(r, gotCanceled && childExited, "假进程被终止并报告取消，而非普通退出/失败");
        return r.ToArray();
    }
}
""";

    private const string MovFallbackFixture = """
using System;
using System.Collections.Generic;
public static class RenderRepairFixture_MovFallback
{
    private static void Check(List<string> r, bool ok, string label)
        => r.Add((ok ? "✓ " : "✗ ") + label);
    public static string[] Run()
    {
        var r = new List<string>();
        int attempts = 0;
        string fallbackArgs = SpectreReplay.Features.VideoRender.MovConversionFallback.BuildH264MovArguments("in.mp4", "out.mov");
        bool fallbackUsed = false;
        bool fallbackArgsValid = false;
        Exception error;
        bool ok = SpectreReplay.Features.VideoRender.MovConversionFallback.TryConvert(
            args =>
            {
                attempts++;
                if (attempts < 3) return new InvalidOperationException(
                    "Could not find tag for codec av1 in container; AV1 only supported in MP4 and AVIF");
                fallbackArgsValid = args.Contains(" -c:v libx264")
                    && !args.Contains(" -c:v copy");
                return null;
            }, "copy", "audio-copy", fallbackArgs,
            () => fallbackUsed = true, out error, out bool used);
        Check(r, ok && used && fallbackUsed && fallbackArgsValid && attempts == 3,
            "AV1→MOV 在音频重试后执行可见的视频 H.264 回退");

        attempts = 0;
        bool unrelated = SpectreReplay.Features.VideoRender.MovConversionFallback.TryConvert(
            args => { attempts++; return new InvalidOperationException("permission denied"); },
            "copy", null, fallbackArgs, null, out error, out used);
        Check(r, !unrelated && attempts == 1 && !used,
            "权限等无关错误不触发回退，也不伪报成功");
        return r.ToArray();
    }
}
""";
}
