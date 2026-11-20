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
        string replayMgr = Read("Features/Replay/Runtime/ReplayManager.cs");

        Console.WriteLine("  ── 写入安全与生命周期 ──");

        Check(atomic.Contains("File.Replace(tmp, path, bak);", StringComparison.Ordinal),
            "① 原子写入使用 File.Replace");
        Check(atomic.Contains("string tmp = path + \".tmp\";", StringComparison.Ordinal)
            && atomic.Contains("string bak = path + \".rolling.bak\";", StringComparison.Ordinal),
            "① 有临时文件与独立滚动备份");
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
        Check(main.Contains("private static bool Teardown()", StringComparison.Ordinal),
            "③ 有统一收尾方法");
        Check(main.Contains("SpectrePatchHub.UnpatchAll();", StringComparison.Ordinal)
            && main.Contains("PatchManager.UnpatchAll();", StringComparison.Ordinal)
            && main.Contains("MainThread.Uninstall();", StringComparison.Ordinal),
            "③ 收尾卸载补丁与线程");
        Check(main.Contains("if (!Features.EffectRemover.EffectRemover.TryPrepareForDisable()) return false;", StringComparison.Ordinal),
            "③ 收尾还原去特效谱面或保留保存保护");
        Check(settings.Contains("Enum.IsDefined(typeof(KeyCode)", StringComparison.Ordinal),
            "④ 热键配置做 Enum.IsDefined 校验");
        Check(compression.Contains("MaxDecompressed / (1024 * 1024)", StringComparison.Ordinal)
            && !compression.Contains("512MB cap", StringComparison.Ordinal),
            "④ 解压上限诊断使用真实 64MB 值");
        // ⑤ 回放是玩家**唯一一份**对局数据。配置与云端回填都走 AtomicFile，唯独回放
        //    保存曾用 FileStream(FileMode.Create) 直写——写到一半崩溃/断电/磁盘满就
        //    留下截断的 .sprp，而末尾 File.Exists 仍为真，调用方据此告诉用户「保存成功」
        //    （2026-11-18 审计 P1-1）。判据钉住「走原子写」与「不再截断目标」两件事。
        Check(replayMgr.Contains("AtomicFile.WriteAllBytes(filePath, data);", StringComparison.Ordinal),
            "⑤ 回放保存走原子写入（与配置/云端同口径）");
        Check(!replayMgr.Contains("FileMode.Create", StringComparison.Ordinal),
            "⑤ 回放保存不再用 FileMode.Create 截断目标文件");

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
