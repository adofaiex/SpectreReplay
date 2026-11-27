using System;

namespace SpectreReplay.Features.VideoRender;

// 发布与收场共用一把锁；Begin 在锁外运行，收场不等待预启动线程。
internal sealed class EncoderPreStartOwnership<T> where T : class
{
    private readonly object gate = new object();
    private bool closed;
    private bool preStartActive;
    private T candidate;
    private T published;
    private T deferredCandidate;

    // Thread.Start 很短；与 Close 同锁，避免清理线程遇到尚未启动的 Thread.Join。
    internal void StartPreStart(Action start)
    {
        if (start == null) throw new ArgumentNullException(nameof(start));
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("Encoder prestart is already closed");
            if (preStartActive) throw new InvalidOperationException("Encoder prestart is already active");
            preStartActive = true;
            try { start(); }
            catch
            {
                preStartActive = false;
                throw;
            }
        }
    }

    internal void EndPreStart()
    {
        lock (gate) preStartActive = false;
    }

    internal bool TryRegister(T value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        lock (gate)
        {
            if (closed) return false;
            if (candidate != null || published != null)
                throw new InvalidOperationException("An encoder candidate is already owned");
            candidate = value;
            return true;
        }
    }

    internal bool TryPublish(T value)
    {
        lock (gate)
        {
            if (closed || !ReferenceEquals(candidate, value)) return false;
            published = value;
            candidate = null;
            return true;
        }
    }

    // 仅预启动线程调用；候选仍由该线程持有，收场不会并发 Dispose(Begin)。
    internal T Abandon(T value)
    {
        lock (gate)
        {
            if (!ReferenceEquals(candidate, value)) return null;
            candidate = null;
            return value;
        }
    }

    // 预启动线程在有界 Dispose 仍遇到活跃泵时，将唯一释放权转交给延迟清理。
    internal bool RetainForDeferredCleanup(T value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        lock (gate)
        {
            if (candidate != null || published != null) return false;
            if (deferredCandidate != null && !ReferenceEquals(deferredCandidate, value)) return false;
            deferredCandidate = value;
            return true;
        }
    }

    internal bool HasDeferredCleanupCandidate
    {
        get { lock (gate) return deferredCandidate != null; }
    }

    // 只能在预启动线程已退出后领取，避免与 Begin 并行 Dispose。
    internal T TakeDeferredCleanupCandidate()
    {
        lock (gate)
        {
            if (preStartActive) return null;
            T result = deferredCandidate;
            deferredCandidate = null;
            return result;
        }
    }

    // 领取与关闭同锁；Close 也能看到最新的主线程字段（含尺寸变化后的 encoder）。
    internal T ClaimPublished(Action<T> claim)
    {
        if (claim == null) throw new ArgumentNullException(nameof(claim));
        lock (gate)
        {
            if (closed || published == null) return null;
            T result = published;
            claim(result);
            published = null;
            return result;
        }
    }

    // 返回已发布 encoder 的唯一收场所有者；candidate 留给预启动线程，收场只延迟删目录。
    internal bool Close(T currentEncoder, out T closingEncoder, out bool preStartStillActive)
    {
        lock (gate)
        {
            preStartStillActive = preStartActive;
            if (closed)
            {
                closingEncoder = null;
                return false;
            }
            closed = true;
            closingEncoder = published ?? currentEncoder;
            published = null;
            return true;
        }
    }

    // 使用收场时的一次状态快照；后续线程退出不能绕过 Dispose 所有权交接。
    internal static bool MustDeferCleanup(bool finalizeStillRunning, bool preStartStillRunning,
        bool candidatePending, bool encoderActiveAtSnapshot)
        => finalizeStillRunning || preStartStillRunning || candidatePending || encoderActiveAtSnapshot;

    // 只有旧编码器已完成 Dispose 且泵确认空闲，才允许启动写同一 raw 路径的新实例。
    // Dispose 异常或活跃状态不确定时保留旧引用，交给 TearDown 的延迟清理。
    internal static bool CanReplaceAfterDispose(T current, Action<T> dispose, Func<T, bool> hasActiveWorker)
    {
        if (current == null) return true;
        if (dispose == null) throw new ArgumentNullException(nameof(dispose));
        if (hasActiveWorker == null) throw new ArgumentNullException(nameof(hasActiveWorker));
        dispose(current);
        try { return !hasActiveWorker(current); }
        catch { return false; }
    }

    // Dispose 前的收场决策与资源交接保持为纯逻辑，离线夹具可验证异常/活跃泵时引用和目录仍被保留。
    internal sealed class CleanupPlan
    {
        internal bool MustDefer { get; }
        internal T DeferredEncoder { get; }
        internal string DeferredTempDir { get; }
        internal Exception DisposeError { get; }

        internal CleanupPlan(bool mustDefer, T deferredEncoder, string deferredTempDir, Exception disposeError)
        {
            MustDefer = mustDefer;
            DeferredEncoder = deferredEncoder;
            DeferredTempDir = deferredTempDir;
            DisposeError = disposeError;
        }
    }

    // 后台收尾的真实释放/删目录操作集中在此处，夹具可用替身 encoder 与真实临时目录执行。
    internal static bool FinishDeferredCleanup(T[] encoders, string tempDir,
        Action<T> dispose, Func<T, bool> hasActiveWorker,
        Func<string, bool> directoryExists, Action<string> deleteDirectory,
        Func<bool> timedOut, Action wait, Action<Exception> report)
    {
        if (dispose == null) throw new ArgumentNullException(nameof(dispose));
        if (hasActiveWorker == null) throw new ArgumentNullException(nameof(hasActiveWorker));
        if (directoryExists == null) throw new ArgumentNullException(nameof(directoryExists));
        if (deleteDirectory == null) throw new ArgumentNullException(nameof(deleteDirectory));
        if (timedOut == null) throw new ArgumentNullException(nameof(timedOut));
        if (wait == null) throw new ArgumentNullException(nameof(wait));
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (encoders != null)
        {
            foreach (T encoder in encoders)
            {
                if (encoder == null) continue;
                while (true)
                {
                    bool disposeFailed = false;
                    try { dispose(encoder); }
                    catch (Exception ex) { report(ex); disposeFailed = true; }
                    bool active;
                    try { active = hasActiveWorker(encoder); }
                    catch (Exception ex) { report(ex); active = true; }
                    if (!disposeFailed && !active) break;
                    if (timedOut()) return false;
                    wait();
                }
            }
        }
        if (!string.IsNullOrEmpty(tempDir))
        {
            try
            {
                if (directoryExists(tempDir))
                    deleteDirectory(tempDir);
            }
            catch (Exception ex)
            {
                report(ex);
                return false;
            }
        }
        return true;
    }

    // 替代 encoder 必须先发布到作业字段，再调用可能失败的 Begin，失败时 TearDown 才能接管它。
    internal static T CreateAndBeginReplacement(Func<T> create, Action<T> publish, Action<T> begin)
    {
        if (create == null) throw new ArgumentNullException(nameof(create));
        if (publish == null) throw new ArgumentNullException(nameof(publish));
        if (begin == null) throw new ArgumentNullException(nameof(begin));
        T replacement = create();
        if (replacement == null) throw new InvalidOperationException("Encoder replacement factory returned null");
        publish(replacement);
        begin(replacement);
        return replacement;
    }

    internal static CleanupPlan CreateCleanupPlan(T closingEncoder, string tempDir, bool alreadyMustDefer,
        Action<T> dispose, Func<T, bool> hasActiveWorker)
    {
        bool mustDefer = alreadyMustDefer;
        Exception disposeError = null;
        if (!mustDefer && closingEncoder != null)
        {
            try { dispose(closingEncoder); }
            catch (Exception ex) { disposeError = ex; }
            bool active = true;
            try
            {
                if (hasActiveWorker != null)
                    active = hasActiveWorker(closingEncoder);
            }
            catch { active = true; }
            mustDefer = disposeError != null || active;
        }
        return new CleanupPlan(mustDefer,
            mustDefer ? closingEncoder : null,
            mustDefer ? tempDir : null,
            disposeError);
    }

    // 最后的删除门：任何仍可能触碰临时目录的线程或编码泵存活时都必须保留。
    internal static bool CanDeleteTemporaryDirectory(
        bool preStartExited, bool finalizeExited, bool encoderIdle)
        => preStartExited && finalizeExited && encoderIdle;
}
