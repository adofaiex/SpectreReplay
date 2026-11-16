using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 2B-0 颜色先导探针的形状检查。
///
/// 探针必须挂在命令行参数上、只在显式传入时创建；它用 ARGBHalf/RGBAHalf 才能
/// 观察到 >1.0 的值。若有人把格式改回 ARGB32，实验会静默变成“测不到超白”。
/// </summary>
internal static class HdrProbeCheck
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string probe = Read("Features/VideoRender/Diagnostics/HdrProbe.cs");
        string main = Read("Main.cs");
        string script = Read("../tools/hdr-probe.ps1");

        Console.WriteLine("  ── 2B-0 HDR 探针 ──");

        Check(probe.Contains("RenderTextureFormat.ARGBHalf", StringComparison.Ordinal)
            && probe.Contains("TextureFormat.RGBAHalf", StringComparison.Ordinal),
            "① 探针使用 ARGBHalf/RGBAHalf（能观察 >1.0）");
        Check(probe.Contains("hdr-probe.txt", StringComparison.Ordinal)
            && probe.Contains("hdr-probe.png", StringComparison.Ordinal),
            "① 探针写出文本报告与 PNG");
        Check(probe.Contains("--spectre-hdr-probe", StringComparison.Ordinal),
            "① 探针由命令行参数触发");
        Check(probe.Contains("conductor.song != null && conductor.song.isPlaying", StringComparison.Ordinal),
            "① 只等歌曲真正在播（排除选曲界面）");
        Check(probe.Contains("if (!rt.Create())", StringComparison.Ordinal),
            "① 检查 ARGBHalf RT 创建，失败不写 ok");
        Check(probe.Contains("nonblack", StringComparison.Ordinal)
            && probe.Contains("max_frame", StringComparison.Ordinal)
            && probe.Contains("2.5f", StringComparison.Ordinal),
            "① 多帧采样/nonblack/max_frame");
        Check(script.Contains("status=", StringComparison.Ordinal)
            && script.Contains("nonblack=", StringComparison.Ordinal)
            && script.Contains("exit 1", StringComparison.Ordinal),
            "② 启动器解析 status/nonblack，失败 exit 1");
        Check(probe.Contains("QualitySettings.activeColorSpace", StringComparison.Ordinal)
            && probe.Contains("colorspace=", StringComparison.Ordinal),
            "① 探针报告色彩空间（PQ 前置判据）");
        Check(probe.Contains("RenderTextureFormat.ARGB32", StringComparison.Ordinal)
            && probe.Contains("RenderTextureFormat.ARGB64", StringComparison.Ordinal)
            && probe.Contains("ab_uniq8=", StringComparison.Ordinal)
            && probe.Contains("ab_uniq10=", StringComparison.Ordinal),
            "④ A/B 对拍：ARGB32/ARGB64 同链双帧 + 量化阶数");
        Check(main.Contains("Features.VideoRender.HdrProbe.TryStart();", StringComparison.Ordinal)
            && main.Contains("Features.VideoRender.HdrProbe.Stop();", StringComparison.Ordinal),
            "② Main 在 Load/Teardown 接线");

        Console.WriteLine($"  2B-0 HDR 探针：{total} 条，失败 {failed}");
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