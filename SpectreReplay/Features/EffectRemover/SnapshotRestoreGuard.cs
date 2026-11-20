using System;

namespace SpectreReplay.Features.EffectRemover;

// 完整回滚只允许覆盖我们留下的那份内容；缺快照或后续编辑一律保留当前数据。
internal static class SnapshotRestoreGuard
{
    internal static bool CanRestore(string strippedSnapshot, string current)
        => !string.IsNullOrEmpty(strippedSnapshot)
            && string.Equals(strippedSnapshot, current, StringComparison.Ordinal);
}
