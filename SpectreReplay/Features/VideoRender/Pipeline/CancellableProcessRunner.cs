using System;
using System.Diagnostics;
using System.Text;

namespace SpectreReplay.Features.VideoRender;

// Shared by render finalization and offline conversion; cancellation never waits on the UI thread.
internal static class CancellableProcessRunner
{
    internal static void RunAndWait(string exePath, string arguments,
        Func<bool> canceled = null, TimeSpan? timeout = null, bool prefixFfmpegArguments = true,
        Action<int> processStarted = null)
    {
        if (canceled != null && canceled())
            throw new OperationCanceledException("process canceled before start");
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = (prefixFfmpegArguments ? "-hide_banner -loglevel warning " : "") + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        var stderr = new StringBuilder();
        var stderrLock = new object();
        using (Process process = Process.Start(info))
        {
            if (process == null)
                throw new InvalidOperationException("failed to start process");
            processStarted?.Invoke(process.Id);
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (stderrLock)
                {
                    stderr.AppendLine(e.Data);
                    if (stderr.Length > 8192)
                        stderr.Remove(0, stderr.Length - 8192);
                }
            };
            process.OutputDataReceived += (_, e) => { };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            CancellableWaitResult result = CancellableWait.Wait(
                milliseconds => process.WaitForExit(milliseconds), canceled,
                timeout ?? TimeSpan.FromMinutes(30), 100);
            if (result == CancellableWaitResult.Canceled)
            {
                TerminateBounded(process);
                throw new OperationCanceledException("process canceled");
            }
            if (result == CancellableWaitResult.TimedOut)
            {
                TerminateBounded(process);
                string tail;
                lock (stderrLock) tail = stderr.ToString();
                throw new TimeoutException("process timed out"
                    + (tail.Length > 0 ? " stderr tail: " + tail : ""));
            }
            // WaitForExit() drains asynchronous stdout/stderr callbacks after process exit.
            process.WaitForExit();
            if (canceled != null && canceled())
                throw new OperationCanceledException("process canceled");
            string stderrText;
            lock (stderrLock) stderrText = stderr.ToString();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("process failed, exit code "
                    + process.ExitCode + ": " + stderrText);
        }
    }

    private static void TerminateBounded(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch { }
        try { process.WaitForExit(3000); } catch { }
    }
}
