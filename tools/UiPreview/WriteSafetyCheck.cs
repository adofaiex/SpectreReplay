using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 写入安全与生命周期收尾的离线形状检查。
///
/// 这类缺陷在游戏里没有任何异常：配置写入被崩溃截断、启用失败后残留半套补丁、
/// 关闭模组时去特效还原没跑——玩家只会觉得「设置丢了」「重开游戏才正常」。
/// </summary>
internal static class WriteSafetyCheck
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string atomic = Read("State/AtomicFile.cs");
        string cfg = Read("State/ConfigManager.cs");
        string cloud = Read("Features/Cloud/CloudReplay.cs");
        string main = Read("Main.cs");
        string settings = Read("State/Settings.cs");
        string compression = Read("Features/Replay/Codec/ReplayCompression.cs");

        Console.WriteLine("  ── 写入安全与生命周期 ──");

        Check(atomic.Contains("File.Replace(tmp, path, bak);", StringComparison.Ordinal),
            "① 原子写入使用 File.Replace");
        Check(atomic.Contains("string tmp = path + \".tmp\";", StringComparison.Ordinal)
            && atomic.Contains("string bak = path + \".bak\";", StringComparison.Ordinal),
            "① 有临时文件与 .bak 备份");
        Check(atomic.Contains("File.Delete(path);", StringComparison.Ordinal)
            && atomic.Contains("File.Move(tmp, path);", StringComparison.Ordinal),
            "① 有删除+移动的回退路径");
        Check(cfg.Contains("AtomicFile.WriteAllText(filePath, contents);", StringComparison.Ordinal),
            "② 配置保存走原子写入");
        Check(cfg.Contains("AtomicFile.WriteAllText(configPath, json.ToString(Formatting.Indented));", StringComparison.Ordinal),
            "② 旧配置迁移走原子写入");
        Check(cloud.Contains("AtomicFile.WriteAllBytes(localPath", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(cloud, @"(?<!Atomic)File\.WriteAllBytes\(localPath"),
            "② 云恢复本地落盘走原子写入");
        Check(main.Contains("private static void Teardown()", StringComparison.Ordinal),
            "③ 有统一收尾方法");
        Check(main.Contains("SpectrePatchHub.UnpatchAll();", StringComparison.Ordinal)
            && main.Contains("PatchManager.UnpatchAll();", StringComparison.Ordinal)
            && main.Contains("MainThread.Uninstall();", StringComparison.Ordinal),
            "③ 收尾卸载补丁与线程");
        Check(main.Contains("Features.EffectRemover.EffectRemover.RestoreOriginalLevelData();", StringComparison.Ordinal),
            "③ 收尾还原去特效谱面");
        Check(settings.Contains("Enum.IsDefined(typeof(KeyCode)", StringComparison.Ordinal),
            "④ 热键配置做 Enum.IsDefined 校验");
        Check(compression.Contains("MaxDecompressed / (1024 * 1024)", StringComparison.Ordinal)
            && !compression.Contains("512MB cap", StringComparison.Ordinal),
            "④ 解压上限诊断使用真实 64MB 值");

        Console.WriteLine($"  写入安全与生命周期：{total} 条，失败 {failed}");
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