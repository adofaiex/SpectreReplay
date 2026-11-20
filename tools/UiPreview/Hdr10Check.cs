using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 2B-2 HDR10 的形状检查。
///
/// HDR10 不是“把 SDR 标成 PQ”：必须 ARGBHalf 采集、zscale 做 sRGB→PQ + BT.709→BT.2020、
/// x265 写 HDR10 元数据。少任何一环，成片要么发灰，要么只是 HDR 容器里的 SDR。
/// </summary>
internal static class Hdr10Check
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string enc = Read("Features/VideoRender/Pipeline/VideoEncoder.cs");
        string fs = Read("Features/VideoRender/Pipeline/VideoFrameSource.cs");
        string job = Read("Features/VideoRender/Pipeline/VideoRenderJob.cs");
        string settings = Read("State/Settings.cs");
        string cfg = Read("State/ConfigManager.cs");
        string pages = Read("Pages/Pages.cs");

        Console.WriteLine("  ── HDR10 ──");

        Check(settings.Contains("public static bool RenderHdr10;", StringComparison.Ordinal),
            "① Settings 有 RenderHdr10");
        Check(cfg.Contains("render_hdr10 = Settings.RenderHdr10", StringComparison.Ordinal)
            && cfg.Contains("ints.Value<bool?>(\"render_hdr10\")", StringComparison.Ordinal),
            "① ConfigManager 持久化 render_hdr10");
        Check(pages.Contains("OnGui.Row(\"render.hdr10\"", StringComparison.Ordinal)
            && pages.Contains("render.hdr10.note", StringComparison.Ordinal),
            "① 界面有 HDR10 开关与限制提示");

        Check(fs.Contains("RenderTextureFormat.ARGBHalf", StringComparison.Ordinal)
            && fs.Contains("GraphicsFormat.R16G16B16A16_SFloat", StringComparison.Ordinal),
            "② 采集用 ARGBHalf + SFloat 回读");
        Check(enc.Contains("\"gbrpf32le\"", StringComparison.Ordinal),
            "② ffmpeg 输入 gbrpf32le（float32 平面 RGB）");
        Check(enc.Contains("ConvertHdrFrame", StringComparison.Ordinal)
            && enc.Contains("HalfToFloatBits", StringComparison.Ordinal),
            "② C# 回读 half→float32 平面转换");
        Check(enc.Contains("zscale=transferin=iec61966-2-1", StringComparison.Ordinal)
            && enc.Contains("transfer=smpte2084", StringComparison.Ordinal)
            && enc.Contains("primaries=bt2020", StringComparison.Ordinal),
            "② zscale 做 sRGB→PQ + BT.2020 转换");
        Check(enc.Contains("-colorspace bt2020nc", StringComparison.Ordinal)
            && enc.Contains("-color_primaries bt2020", StringComparison.Ordinal)
            && enc.Contains("-color_trc smpte2084", StringComparison.Ordinal),
            "② 输出 HDR10 元数据");
        Check(enc.Contains("hdr10=1:repeat-headers=1", StringComparison.Ordinal),
            "② x265 写 HDR10 元数据");
        Check(enc.Contains("if (hdr10) return Codec.X265;", StringComparison.Ordinal),
            "③ HDR10 固定软件 H.265");
        Check(!enc.Contains("hdr10 && hardware", StringComparison.Ordinal)
            && !enc.Contains("hdr10 && candidate", StringComparison.Ordinal),
            "③ HDR10 判定不与硬件候选/后端耦合（元数据注入只有 x265 已验证）");
        Check(job.Contains("bool hdr10 = options.Hdr10;", StringComparison.Ordinal)
            && job.Contains("frames.DrawCursor = options.Cursor && !tenBit && !hdr10;", StringComparison.Ordinal),
            "③ 渲染作业传 hdr10 并禁用光标");
        Check(job.Contains("HDR10 需要 CleanCamera", StringComparison.Ordinal),
            "③ WholeScreen 跳过 HDR10 时有日志");

        Console.WriteLine($"  HDR10：{total} 条，失败 {failed}");
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
