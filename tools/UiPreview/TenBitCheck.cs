using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 2B-1 10-bit SDR 的形状检查。
///
/// 10-bit 的关键是整条链都用 8 字节/像素：ARGB64 采集、GraphicsFormat 回读、
/// rgba64le 喂 ffmpeg、yuv420p10le 输出。任何一处仍按 4 字节，轻则花屏，
/// 重则把 8 字节缓冲当 4 字节写坏。
/// </summary>
internal static class TenBitCheck
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string fs = Read("Features/VideoRender/Pipeline/VideoFrameSource.cs");
        string enc = Read("Features/VideoRender/Pipeline/VideoEncoder.cs");
        string job = Read("Features/VideoRender/Pipeline/VideoRenderJob.cs");
        string settings = Read("State/Settings.cs");
        string cfg = Read("State/ConfigManager.cs");
        string pages = Read("Pages/Pages.cs");

        Console.WriteLine("  ── 10-bit SDR ──");

        Check(settings.Contains("public static bool Render10Bit;", StringComparison.Ordinal),
            "① Settings 有 Render10Bit");
        Check(cfg.Contains("render_10bit = Settings.Render10Bit", StringComparison.Ordinal)
            && cfg.Contains("ints.Value<bool?>(\"render_10bit\")", StringComparison.Ordinal),
            "① ConfigManager 持久化 render_10bit");
        Check(pages.Contains("OnGui.Row(\"render.10bit\"", StringComparison.Ordinal),
            "① 界面有 10-bit 开关");
        Check(pages.Contains("render.10bit.screen_note", StringComparison.Ordinal),
            "① WholeScreen 来源下 10-bit 有收益提示");

        Check(fs.Contains("RenderTextureFormat.ARGB64", StringComparison.Ordinal)
            && fs.Contains("GraphicsFormat.R16G16B16A16_UNorm", StringComparison.Ordinal),
            "② 采集用 ARGB64 + GraphicsFormat 回读");
        Check(fs.Contains("if (bytesPerPixel == 8) limit = Math.Max(2, limit - 1);", StringComparison.Ordinal),
            "② MaxInFlight 先按像素，再对 10-bit 减一档");
        Check(fs.Contains("bytesPerPixel = tenBit ? 8 : 4;", StringComparison.Ordinal)
            && fs.Contains("frameBytes = Width * Height * bytesPerPixel;", StringComparison.Ordinal),
            "② 帧字节按 8/4 切换");
        Check(enc.Contains("\"rgba64le\"", StringComparison.Ordinal)
            && enc.Contains("\"yuv420p10le\"", StringComparison.Ordinal),
            "② ffmpeg 输入 rgba64le / 输出 yuv420p10le");
        Check(enc.Contains("QueueCapacity = Math.Max(2, Math.Min(64, 268435456 / (width * height * bytesPerPixel)));", StringComparison.Ordinal),
            "② 队列容量按 bytesPerPixel 限容");
        Check(enc.Contains("if (tenBit && candidate == Codec.QsvH264)", StringComparison.Ordinal),
            "② h264_qsv 无 10-bit 输入，10-bit 时跳过");
        Check(job.Contains("bool tenBit = Settings.Render10Bit;", StringComparison.Ordinal)
            && job.Contains("Settings.RenderHeight, tenBit)", StringComparison.Ordinal),
            "③ 渲染作业把 tenBit 传给帧源");
        Check(job.Contains("frames.DrawCursor = Settings.RenderCursorOn && !tenBit;", StringComparison.Ordinal),
            "③ 10-bit 模式禁用光标叠加");

        Console.WriteLine($"  10-bit SDR：{total} 条，失败 {failed}");
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