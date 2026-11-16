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

        Check(fx.Contains("private static string OriginalLevelDataJson;", StringComparison.Ordinal),
            "① 保存原始谱面 JSON");
        Check(fx.Contains("private static LevelData LastLevelData;", StringComparison.Ordinal)
            && fx.Contains("private static bool Restoring;", StringComparison.Ordinal),
            "① 记住实例与还原中标志");
        Check(fx.Contains("string json = levelData.Encode();", StringComparison.Ordinal),
            "① 改写前调用 LevelData.Encode");
        Check(fx.Contains("if (Restoring)", StringComparison.Ordinal),
            "① Decode Postfix 在还原时跳过 Remove");
        Check(fx.Contains("GDMiniJSON.Json.Deserialize(json)", StringComparison.Ordinal)
            && fx.Contains("data.Decode(dict, out _);", StringComparison.Ordinal),
            "② 用游戏同款 GDMiniJSON 还原 LevelData");
        Check(fx.Contains("internal static void RestoreOriginalLevelData()", StringComparison.Ordinal),
            "② 有显式还原入口");
        Check(main.Contains("Features.EffectRemover.EffectRemover.RestoreOriginalLevelData();", StringComparison.Ordinal),
            "③ 关闭模组时调用还原");
        Check(pages.Contains("EffectRemover.RestoreOriginalLevelData();", StringComparison.Ordinal),
            "③ 关闭去特效开关时调用还原");
        Check(fx.Contains("OriginalLevelDataJson = null;", StringComparison.Ordinal)
            && fx.Contains("LastLevelData = null;", StringComparison.Ordinal),
            "③ 还原成功后清空快照");

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
        => SrcUtil.Clean(File.ReadAllText(Path.Combine(Driver.ProjectDir, rel)));
}