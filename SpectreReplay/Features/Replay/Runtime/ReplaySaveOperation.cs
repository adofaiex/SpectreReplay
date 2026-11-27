using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace SpectreReplay.Features.Replay;

// 一次已接受的保存拥有独立快照和录音文件。只做 System/容器工作，不读 Settings 或 Unity。
// 关闭模组只停止 UI 泵，不撤销已经接受的落盘；主线程完成观察器独立存活到收尾。
internal sealed class ReplaySaveOperation
{
    private const int MaxEmbedPayloadBytes = 4 * 1024 * 1024;
    private static readonly object SaveLock = new object();
    private static readonly object PendingLock = new object();
    private static readonly HashSet<ReplaySaveOperation> Pending = new HashSet<ReplaySaveOperation>();
    private readonly ReplayData data;
    private readonly string directory, safeName, backupDirectory;
    private readonly bool noEncryption, withSound, compact;
    internal volatile bool IsCompleted;
    internal bool Success { get; private set; }
    internal string SavedPath { get; private set; }
    internal string WavPath { get; private set; }
    internal string Error { get; private set; }
    internal bool AudioFailed { get; private set; }
    internal bool BackupFailed { get; private set; }
    internal int WorkerThreadId { get; private set; }

    // 接受请求至主线程发布结果的完整生命周期，不只统计已开始写盘的线程。
    internal static bool HasPendingSaves
    {
        get { lock (PendingLock) return Pending.Count != 0; }
    }

    internal void Accept()
    {
        lock (PendingLock) Pending.Add(this);
    }

    internal void Release()
    {
        lock (PendingLock) Pending.Remove(this);
    }

    internal ReplaySaveOperation(ReplayData snapshot, string directory, string safeName,
        bool noEncryption, bool withSound, bool compact, string backupDirectory)
    {
        data = snapshot;
        this.directory = directory;
        this.safeName = safeName;
        this.noEncryption = noEncryption;
        this.withSound = withSound;
        this.compact = compact;
        this.backupDirectory = backupDirectory;
    }

    internal void Start()
    {
        if (!ThreadPool.QueueUserWorkItem(_ => Run()))
            throw new InvalidOperationException("Replay save worker could not be queued");
    }

    internal void AllowRetry()
    {
        if (!Success && data.KeybdSoundStop != null) data.KeybdSoundStop.SaveQueued = false;
    }

    private void Run()
    {
        // AtomicFile 的同目标 .tmp 名固定；跨局排队不能并发争用同一目标。
        lock (SaveLock) RunCore();
    }

    private void RunCore()
    {
        WorkerThreadId = Thread.CurrentThread.ManagedThreadId;
        try
        {
            byte[] wav = null;
            if (withSound && !data.KeybdAudio.HasData && !string.IsNullOrEmpty(data.KeybdSoundFileName))
            {
                try
                {
                    string path = data.KeybdSoundStop?.AudioPath ?? data.KeybdSoundSourcePath;
                    wav = data.KeybdSoundMemory ?? (path == null ? null : File.ReadAllBytes(path));
                    if (!VerifyWav(wav, data.KeybdSoundHash))
                    {
                        wav = null;
                        AudioFailed = true;
                    }
                    else
                    {
                        int quality = compact ? KeybdAudioCodec.QualityExtreme : KeybdAudioCodec.QualityBalanced;
                        var blob = KeybdAudioCodec.Encode(wav, quality, out _);
                        if (quality != KeybdAudioCodec.QualityExtreme && blob.Payload != null
                            && blob.Payload.Length > MaxEmbedPayloadBytes)
                            blob = KeybdAudioCodec.Encode(wav, KeybdAudioCodec.QualityExtreme, out _);
                        if (blob.HasData && blob.Payload.Length <= MaxEmbedPayloadBytes) data.KeybdAudio = blob;
                    }
                }
                catch (Exception ex) { AudioFailed = true; Error = ex.ToString(); }
            }
            if (!withSound) data.KeybdAudio = default;
            string originalName = data.FileName;
            if (!TryWrite(originalName))
            {
                data.FileName = safeName;
                if (data.KeybdSoundFileName != null) data.KeybdSoundFileName = safeName + ".wav";
                if (!TryWrite(safeName)) return;
            }
            Success = true;
            if (withSound && !data.KeybdAudio.HasData && wav != null)
            {
                try
                {
                    WavPath = Path.Combine(directory, data.KeybdSoundFileName);
                    AtomicFile.WriteAllBytes(WavPath, wav);
                }
                catch (Exception ex) { WavPath = null; AudioFailed = true; Error = ex.ToString(); }
            }
            if (backupDirectory != null)
            {
                try
                {
                    Directory.CreateDirectory(backupDirectory);
                    File.Copy(SavedPath, Path.Combine(backupDirectory, Path.GetFileName(SavedPath)), true);
                    if (WavPath != null)
                        File.Copy(WavPath, Path.Combine(backupDirectory, Path.GetFileName(WavPath)), true);
                }
                catch (Exception ex) { BackupFailed = true; Error = ex.ToString(); }
            }
            // 只删本作业拥有的文件；失败保留，绝不触碰下一局的新 tmp.wav。
            string owned = data.KeybdSoundStop?.AudioPath;
            if (owned != null && (!withSound || data.KeybdAudio.HasData || WavPath != null))
            {
                try { File.Delete(owned); }
                catch (Exception ex) { Error = ex.ToString(); }
            }
        }
        catch (Exception ex) { Error = ex.ToString(); }
        finally { IsCompleted = true; }
    }

    private bool TryWrite(string name)
    {
        try
        {
            var replay = ReplayDecoder.FromReplayData(data);
            replay.EndTime = DateTimeOffset.UtcNow;
            byte[] bytes = ReplayEncoder.Encode(replay);
            if (!noEncryption) bytes = ReplayCrypto.EncryptCore(bytes);
            string path = Path.GetFullPath(Path.Combine(directory, name)
                + (noEncryption ? ReplayConstants.ExtensionNoEncrypt : ReplayConstants.Extension));
            AtomicFile.WriteAllBytes(path, bytes);
            SavedPath = path;
            return true;
        }
        catch (Exception ex) { Error = ex.ToString(); return false; }
    }

    private static bool VerifyWav(byte[] wav, string expectedHash)
    {
        if (wav == null || wav.Length <= 44 || string.IsNullOrEmpty(expectedHash)) return false;
        using var sha = SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(wav, 44, wav.Length - 44))
            .Replace("-", "").ToLowerInvariant();
        return string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase);
    }
}
