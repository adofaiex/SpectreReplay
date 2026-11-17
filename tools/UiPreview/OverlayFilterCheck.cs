using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 2D 组件类型排除的形状检查。
///
/// 不手填 GameObject 名；按组件类型全名/短名排除。配置需要数组持久化，
/// 且 UI 必须显示「已排除 N 个对象」，否则玩家配置错了会以为功能没生效。
/// </summary>
internal static class OverlayFilterCheck
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string settings = Read("State/Settings.cs");
        string cfg = Read("State/ConfigManager.cs");
        string overlay = Read("Features/VideoRender/Pipeline/RenderOverlayCapture.cs");
        string pages = Read("Pages/Pages.cs");

        Console.WriteLine("  ── 组件类型过滤 ──");

        Check(settings.Contains("RenderOverlayExcludeTypes", StringComparison.Ordinal)
            && settings.Contains("Distinct(StringComparer.Ordinal)", StringComparison.Ordinal),
            "① Settings 有组件类型排除表并归一化");
        Check(cfg.Contains("render_overlay_exclude_types = Settings.RenderOverlayExcludeTypes,", StringComparison.Ordinal)
            && cfg.Contains("ParseOverlayTypes", StringComparison.Ordinal),
            "① ConfigManager 数组持久化");
        Check(overlay.Contains("if (IsExcluded(e.Canvas))", StringComparison.Ordinal)
            && overlay.Contains("GetComponents<Component>()", StringComparison.Ordinal),
            "② 按组件类型全名/短名排除");
        Check(overlay.Contains("ExcludedCount()", StringComparison.Ordinal),
            "② 有「已匹配 N 个对象」计数");
        Check(pages.Contains("OnGui.RowTextField(\"render.overlay_exclude\"", StringComparison.Ordinal)
            && pages.Contains("render.overlay_excluded", StringComparison.Ordinal),
            "③ UI 有自绘输入框与匹配数量提示");

        Console.WriteLine($"  组件类型过滤：{total} 条，失败 {failed}");
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