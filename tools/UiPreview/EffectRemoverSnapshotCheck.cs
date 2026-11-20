using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 去特效应答安全：改写内存谱面前必须有原始快照，关闭模组/功能时能原地还原。
/// 这类缺陷在游戏里没有任何异常：玩家关掉去特效再保存，写回的是剥过特效的版本。
/// </summary>
internal static class EffectRemoverSnapshotCheck
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string fx = Read("Features/EffectRemover/EffectRemover.cs");
        string main = Read("Main.cs");
        string pages = Read("Pages/Pages.cs");

        Console.WriteLine("  ── 去特效原始快照 ──");

        Check(fx.Contains("Safety.Track(levelData, json);", StringComparison.Ordinal)
            && Read("Features/EffectRemover/EffectDataSafety.cs").Contains("internal string Original;", StringComparison.Ordinal),
            "① 按谱面对象保存原始谱面 JSON");
        Check(fx.Contains("private static bool Restoring;", StringComparison.Ordinal)
            && Read("Features/EffectRemover/EffectDataSafety.cs").Contains("ConditionalWeakTable<object, Snapshot>", StringComparison.Ordinal),
            "① 保护状态随数据对象存活并跨副本复制");
        Check(fx.Contains("string json = levelData.Encode();", StringComparison.Ordinal),
            "① 改写前调用 LevelData.Encode");
        Check(fx.Contains("if (Restoring)", StringComparison.Ordinal),
            "① Decode Postfix 在还原时跳过 Remove");
        Check(fx.Contains("GDMiniJSON.Json.Deserialize(json)", StringComparison.Ordinal)
            && fx.Contains("data.Decode(dict, out LoadResult status);", StringComparison.Ordinal),
            "② 用游戏同款 GDMiniJSON 还原 LevelData");
        Check(fx.Contains("internal static void RestoreOriginalLevelData()", StringComparison.Ordinal),
            "② 有显式还原入口");
        Check(main.Contains("if (!Features.EffectRemover.EffectRemover.TryPrepareForDisable()) return false;", StringComparison.Ordinal)
            && fx.Contains("RestoreOriginalLevelData();", StringComparison.Ordinal),
            "③ 关闭模组前还原或交接保存保护");
        Check(pages.Contains("EffectRemover.RestoreOriginalLevelData();", StringComparison.Ordinal),
            "③ 关闭去特效开关时调用还原");
        Check(fx.Contains("Safety.MarkClean(data);", StringComparison.Ordinal)
            && fx.Contains("Safety.IsStripped(state.data)", StringComparison.Ordinal),
            "③ 仅还原当前对象，保留撤销历史中的去特效副本记录");

        Console.WriteLine($"  去特效原始快照：{total} 条，失败 {failed}");
        return (total, failed);

        void Check(bool ok, string label)
        {
            total++;
            Console.WriteLine((ok ? "    ✓ " : "    ✗ ") + label);
            if (!ok) failed++;
        }
    }

    private static string Read(string rel)
        => SrcUtil.Source(Driver.ProjectDir, rel);
}
