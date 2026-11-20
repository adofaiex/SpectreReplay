using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace SpectreReplay.Features.EffectRemover;

// 保存状态随 LevelData.Copy 传递，不能由一个全局 bool 代表整条撤销历史。
// 弱键不延长已丢弃的谱面/历史副本生命周期；同源副本共享只读快照与载入路径。
internal sealed class EffectDataSafety
{
    internal sealed class Snapshot
    {
        internal string Original;
        internal string Stripped;
        internal string SourcePath;
        internal LevelFileIdentity? SourceIdentity;
        internal bool SourceKnown;
    }

    private readonly ConditionalWeakTable<object, Snapshot> snapshots = new ConditionalWeakTable<object, Snapshot>();

    internal void Track(object data, string original)
    {
        MarkClean(data);
        snapshots.Add(data, new Snapshot { Original = original });
    }

    internal bool TryGet(object data, out Snapshot snapshot)
    {
        snapshot = null;
        return data != null && snapshots.TryGetValue(data, out snapshot);
    }

    internal bool IsStripped(object data) => TryGet(data, out _);

    internal bool Restore(object data, string current, Func<string, bool> decode)
    {
        if (!TryGet(data, out Snapshot snapshot)
            || !SnapshotRestoreGuard.CanRestore(snapshot.Stripped, current)) return false;
        if (!decode(snapshot.Original)) return false;
        MarkClean(data);
        return true;
    }

    internal void MarkClean(object data)
    {
        if (data != null) snapshots.Remove(data);
    }

    internal void Copy(object source, object copy)
    {
        if (copy == null || ReferenceEquals(source, copy)) return;
        MarkClean(copy);
        if (TryGet(source, out Snapshot snapshot)) snapshots.Add(copy, snapshot);
    }

    internal void SetStrippedSnapshot(object data, string stripped)
    {
        if (TryGet(data, out Snapshot snapshot)) snapshot.Stripped = stripped;
    }

    // LoadLevel 的实参来自读盘入口；不能在禁用/Save As 后从可变的 levelPath 追记原路径。
    internal void BindSource(object data, string path)
    {
        if (!TryGet(data, out Snapshot snapshot)) return;
        // 固定相对路径的解析基准；原路径被移动或替换后仍要保护其已有硬链接。
        snapshot.SourceKnown = false;
        snapshot.SourceIdentity = null;
        try
        {
            if (!string.IsNullOrEmpty(path))
            {
                path = Path.GetFullPath(path);
                LevelFileIdentity.TryRead(path, out snapshot.SourceIdentity);
            }
            snapshot.SourcePath = path;
            snapshot.SourceKnown = true;
        }
        catch { /* 无法确认来源时继续保护。 */ }
    }

    internal bool CanWrite(object data, string target, bool explicitOverride)
    {
        if (explicitOverride || !TryGet(data, out Snapshot snapshot)) return true;
        if (string.IsNullOrEmpty(target)) return true; // SaveLevel 只会打开另存为对话框。
        if (!snapshot.SourceKnown) return false;
        if (string.IsNullOrEmpty(snapshot.SourcePath)) return true;
        try
        {
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string fullTarget = Path.GetFullPath(target);
            if (string.Equals(fullTarget, snapshot.SourcePath, comparison)) return false;
            if (!LevelFileIdentity.TryRead(fullTarget, out var targetIdentity)) return false;
            if (!targetIdentity.HasValue) return true;

            if (!snapshot.SourceIdentity.HasValue) return false;
            if (!LevelFileIdentity.TryRead(snapshot.SourcePath, out var currentSourceIdentity)) return false;
            return !targetIdentity.Equals(snapshot.SourceIdentity) && !targetIdentity.Equals(currentSourceIdentity);
        }
        catch { return false; } // 无法证明目标与原文件不同，就不写。
    }

    internal bool CanWriteCurrentSource(object data, string target, bool explicitOverride)
        => CanWrite(data, target, explicitOverride);
}
