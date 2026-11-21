using System;

namespace SpectreReplay;

// 只在主线程消费。后台 WAV 线程结束后，AudioRecorder.Cleanup 才发布结果。
internal sealed class RecordingStop
{
    internal bool IsCompleted { get; private set; }
    internal string Hash { get; private set; }
    internal bool SaveQueued;
    private Action<string> completed;

    internal void WhenCompleted(Action<string> callback)
    {
        if (IsCompleted) callback(Hash);
        else completed += callback;
    }

    internal void Complete(string hash)
    {
        if (IsCompleted) return;
        Hash = hash;
        IsCompleted = true;
        Action<string> callback = completed;
        completed = null;
        callback?.Invoke(hash);
    }
}
