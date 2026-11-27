using System;
using System.Diagnostics;

namespace SpectreReplay.Features.VideoRender;

internal enum CancellableWaitResult
{
    Completed,
    Canceled,
    TimedOut
}

// The waiter receives a bounded poll interval (rather than blocking for the whole timeout).
internal static class CancellableWait
{
    internal static CancellableWaitResult Wait(Func<int, bool> poll, Func<bool> canceled,
        TimeSpan timeout, int pollMilliseconds = 100)
    {
        if (poll == null) throw new ArgumentNullException(nameof(poll));
        var clock = Stopwatch.StartNew();
        int interval = Math.Max(1, pollMilliseconds);
        while (true)
        {
            if (poll(interval)) return CancellableWaitResult.Completed;
            if (canceled != null && canceled()) return CancellableWaitResult.Canceled;
            if (clock.Elapsed >= timeout) return CancellableWaitResult.TimedOut;
        }
    }
}
