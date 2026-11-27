using System;
using System.IO;

namespace SpectreReplay;

// 只在主线程消费。后台 WAV 线程结束后，AudioRecorder.Cleanup 才发布结果。
internal sealed class RecordingStop
{
    internal bool IsCompleted { get; private set; }
    internal string Hash { get; private set; }
    internal bool SaveQueued;
    // 封头线程退出前把固定 tmp.wav 转交成每局独占的文件；保存重试/下一局绝不争用。
    internal string AudioPath { get; private set; }

    internal void PreserveAudio(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return;
        string owned = sourcePath + ".recording_" + Guid.NewGuid().ToString("N") + ".wav";
        File.Move(sourcePath, owned);
        AudioPath = owned;
    }

    internal void DiscardUnclaimedAudio()
    {
        if (!SaveQueued && AudioPath != null && File.Exists(AudioPath))
            File.Delete(AudioPath);
    }
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
