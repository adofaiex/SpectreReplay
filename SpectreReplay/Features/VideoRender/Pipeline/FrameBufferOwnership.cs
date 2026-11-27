using System;
using System.Collections.Generic;

namespace SpectreReplay.Features.VideoRender;

// 仅保护 buffer 所有权状态与池；大帧复制由调用方在方法返回后完成，不持锁。
internal sealed class FrameBufferOwnership : IDisposable
{
    private readonly object gate = new object();
    private readonly Queue<byte[]> spares = new Queue<byte[]>();
    private readonly Dictionary<byte[], int> substituteReaders = new Dictionary<byte[], int>();
    private readonly HashSet<byte[]> completedPending = new HashSet<byte[]>();
    private byte[] heldFrame;
    private byte[] pendingReturn;
    private bool heldWritten;
    private bool disposed;
    private int spareLimit;

    internal FrameBufferOwnership(int spareLimit)
        => this.spareLimit = Math.Max(1, spareLimit);

    // 同一临界区内先退役旧源、再租借并登记新源。泵回调不能在两步之间
    // 把刚退役的 buffer 提前借给失败帧。
    internal byte[] RentForFrame(int size)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            RetireHeldLocked();
            byte[] pixels = RentLocked(size);
            heldFrame = pixels;
            heldWritten = false;
            return pixels;
        }
    }

    // 只在锁内快照源并租借目的数组；整帧复制由调用者在锁外执行。
    internal bool TryRentSubstitute(int size, out byte[] source, out byte[] destination)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            source = heldFrame;
            if (source == null)
            {
                destination = null;
                return false;
            }
            destination = RentLocked(size);
            substituteReaders.TryGetValue(source, out int readers);
            substituteReaders[source] = readers + 1;
            return true;
        }
    }

    // 调用方已在锁内 pin 源；复制结束后解除 pin 并提交延迟回收。
    internal void ReleaseSubstituteSource(byte[] source)
    {
        if (source == null) return;
        lock (gate)
        {
            if (!substituteReaders.TryGetValue(source, out int readers)) return;
            if (readers > 1)
            {
                substituteReaders[source] = readers - 1;
                return;
            }
            substituteReaders.Remove(source);
            if (!disposed && source != heldFrame && completedPending.Remove(source))
                ReturnLocked(source);
        }
    }

    // 编码泵线程回调。比较、置位、挂起回收与入池原子化；
    // ConcurrentQueue 只能保证队列结构安全，不能代替所有权协议。
    internal void RecycleOrHold(byte[] pixels)
    {
        if (pixels == null) return;
        lock (gate)
        {
            if (disposed) return;
            if (pixels == heldFrame)
            {
                heldWritten = true;
                return;
            }
            if (pixels == pendingReturn)
                pendingReturn = null;
            ReturnCompletedLocked(pixels);
        }
    }

    internal void Recycle(byte[] pixels)
    {
        if (pixels == null) return;
        lock (gate)
        {
            if (!disposed) ReturnCompletedLocked(pixels);
        }
    }

    internal void SetSparePoolLimit(int limit)
    {
        lock (gate)
        {
            if (disposed) return;
            spareLimit = Math.Max(spareLimit, limit);
            while (spares.Count > spareLimit)
                spares.Dequeue();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            heldFrame = null;
            pendingReturn = null;
            heldWritten = false;
            spares.Clear();
            substituteReaders.Clear();
            completedPending.Clear();
        }
    }

    private void RetireHeldLocked()
    {
        if (heldFrame == null) return;
        if (heldWritten)
            ReturnCompletedLocked(heldFrame);
        else
            // 最多只保留一个快捷跟踪位。旧的待回收 buffer 仍归其回调所有；
            // 若该回调晚到，它不再匹配本槽，按普通完成帧安全入池。
            pendingReturn = heldFrame;
        heldFrame = null;
        heldWritten = false;
    }

    private void ReturnCompletedLocked(byte[] pixels)
    {
        if (substituteReaders.ContainsKey(pixels))
            completedPending.Add(pixels);
        else
            ReturnLocked(pixels);
    }

    private byte[] RentLocked(int size)
        => spares.Count > 0 ? spares.Dequeue() : new byte[size];

    private void ReturnLocked(byte[] pixels)
    {
        if (spares.Count < spareLimit)
            spares.Enqueue(pixels);
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(FrameBufferOwnership));
    }
}
