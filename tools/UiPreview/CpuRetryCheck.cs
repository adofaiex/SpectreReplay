using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 后端中途失败 → CPU 重跑的形状检查。
///
/// 这类缺陷在游戏里表现为「渲染跑了半小时，最后一分钟失败，然后什么都没发生」。
/// 重跑钩子必须挂在终态上报之后，否则会被 StartRender 的 Job != null 守卫挡掉。
/// </summary>
internal static class CpuRetryCheck
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string vr = Read("Features/VideoRender/Pipeline/VideoRender.cs");
        string job = Read("Features/VideoRender/Pipeline/VideoRenderJob.cs");
        string enc = Read("Features/VideoRender/Pipeline/VideoEncoder.cs");
        string pages = Read("Pages/Pages.cs");

        Console.WriteLine("  ── CPU 重跑 ──");

        Check(enc.Contains("internal bool IsBackendFailure()", StringComparison.Ordinal),
            "① VideoEncoder 能识别后端失败");
        Check(enc.Contains("\"Error while opening encoder\"", StringComparison.Ordinal)
            && enc.Contains("\"Unknown encoder\"", StringComparison.Ordinal)
            && enc.Contains("\"Could not open encoder\"", StringComparison.Ordinal),
            "① 失败特征覆盖编码器不可用");
        Check(!enc.Contains("\"Invalid argument\"", StringComparison.Ordinal)
            && !enc.Contains("\"not supported\"", StringComparison.Ordinal)
            && !enc.Contains("\"Specified pixel format\"", StringComparison.Ordinal),
            "① 特征表不含裸 Invalid argument / not supported / pixel format");

        Check(vr.Contains("internal static void RequestCpuRetry(", StringComparison.Ordinal)
            && vr.Contains("internal static void StartCpuRetry(", StringComparison.Ordinal),
            "② VideoRender 有请求/启动重跑");
        Check(vr.Contains("CpuRetryUsed", StringComparison.Ordinal)
            && vr.Contains("ForceCpuNextRun", StringComparison.Ordinal),
            "② 只自动重跑一次 + 强制 CPU");

        Check(job.Contains("encoder.IsBackendFailure()", StringComparison.Ordinal)
            && job.Contains("VideoRender.RequestCpuRetry(", StringComparison.Ordinal),
            "③ NotifyFailed 触发重跑");
        Check(job.Contains("VideoRender.StartCpuRetry(retryRange, retryReason);", StringComparison.Ordinal)
            && job.Contains("SpectreAPI.RaiseRenderFinished(endStatus, endOutput, endError);", StringComparison.Ordinal),
            "③ 重跑钩子在终态上报之后");
        Check(job.Contains("job.hardware = VideoRender.ForceCpuNextRun", StringComparison.Ordinal),
            "③ 作业在 Launch 捕获 CPU 后端");
        Check(pages.Contains("if (!string.IsNullOrEmpty(s.Detail))", StringComparison.Ordinal)
            && pages.Contains("GUILayout.Label(s.Detail, Gui.NoteStyle)", StringComparison.Ordinal),
            "④ 重跑原因显示在阶段下");

        Console.WriteLine($"  CPU 重跑：{total} 条，失败 {failed}");
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