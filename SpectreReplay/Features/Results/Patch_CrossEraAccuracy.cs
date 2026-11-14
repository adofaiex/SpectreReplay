// 共享库：SpectrePatch 属性 / PatchType / PropertyAccessor / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Globalization;
using UnityEngine;

namespace SpectreReplay.Features.Results;

/// <summary>
/// 结算详情在精准度行内联标注「3.3.1 口径」的准度（2026-11-14 用户需求）：
/// 3.4.0 上显示 ≤3.3.1 口径——同一份判定分布在 r148 官方公式下的取值，
/// 玩家不用换游戏版本对比。
///
/// <para><b>展示范围（2026-11-14 用户裁定收敛）</b>：只在 3.4.0 本机展示
/// 3.3.1 口径；3.3.1 本机**不展示** 3.4.0 口径。口径差异是游戏自身的行为
/// 差异，Legacy 公式在 <see cref="SpectreReplay.Features.Compat.HitMarginCompat"/>
/// 按 r148 反编译复刻（Auto/中旋计 1.0、Multipress 只进分母、FailedFloor
/// 顶替 deadTiles 项）。</para>
///
/// <para><b>跟哪一行比。</b>游戏结算那行按 <c>Persistence.showXAccuracy</c>
/// 二选一显示 X-Accuracy 或 Accuracy；内联值跟随同一开关。xacc 的
/// checkpoint 惩罚（0.9875^次数）与官方 percentXAcc 同规则——编辑器试玩不罚
///（scrMarginTracker.cs:216）。</para>
///
/// <para><b>显示形态。</b>内联在精准度行的 % 号后（玩家反馈单独一行太占
/// 空间；且末尾追加会落到 TMP 可见区外——2026-11-14 实测）。定位用游戏
/// 自己的本地化标签（RDString，语言无关），找不到就不加并记日志。</para>
/// </summary>
internal static class Patch_CrossEraAccuracy
{
    [SpectrePatch(typeof(DetailedResults), "GenerateResults", PatchType.Postfix,
        Feature = FeatureKeys.CrossEraAccuracy)]
    internal static void Postfix(scrMarginTracker marginTracker, ref string __result)
    {
        if (marginTracker == null || __result == null)
            return;
        // 用户裁定（2026-11-14）：跨口径对照只在 3.4.0 上展示（3.3.1 口径）；
        // 3.3.1 本机不展示 3.4.0 口径。
        if (!SpectreReplay.Features.Compat.HitMarginCompat.HasXPerfect)
            return;
        int[] counts = marginTracker.hitMarginsCount;

        // 与上一行同一开关；数值格式跟 Legacy 结算显示惯例（两位小数）。
        if (Persistence.showXAccuracy)
        {
            double x = SpectreReplay.Features.Compat.HitMarginCompat.LegacyXAcc(counts);
            if (x < 0.0)
                return;
            if (scnEditor.instance == null)
                x *= System.Math.Pow(0.9875, scrController.checkpointsUsed);
            InsertInline(ref __result,
                (x * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%");
        }
        else
        {
            double a = SpectreReplay.Features.Compat.HitMarginCompat.LegacyAccuracy(counts);
            if (a < 0.0)
                return;
            InsertInline(ref __result,
                (a * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%");
        }
    }

    // 插入位置：**紧跟游戏自己的精准度行**（两条口径相邻，直接可比）。内联标签
    // 只写版本号「3.3.1」（语言无关，不搬面板的完整文案——玩家反馈太长）。
    // 定位用游戏自己的本地化标签（RDString，语言无关）；找不到（游戏文本
    // 结构变了）时退回文本开头。**不能追加到末尾**：2026-11-14 实测，Postfix 已挂载
    //（Player.log hub 挂载记录可证）、行也确实写进了字符串，但末尾一行落在
    // TMP 可见区域之外，屏幕上看不到——结算块尾部最容易被裁。
    private static void InsertInline(ref string text, string value)
    {
        string accuracyLabel = RDString.Get(Persistence.showXAccuracy
            ? "status.results.xAccuracy"
            : "status.results.accuracy") + ": ";
        int idx = text.IndexOf(accuracyLabel, StringComparison.Ordinal);
        if (idx < 0)
        {
            Debug.LogWarning("[SpectreReplay] 结算跨口径：找不到精准度行（游戏文本结构变了），本次不加");
            return;
        }
        int pct = text.IndexOf('%', idx + accuracyLabel.Length);
        if (pct < 0)
        {
            Debug.LogWarning("[SpectreReplay] 结算跨口径：精准度行里没有 %（游戏文本结构变了），本次不加");
            return;
        }
        text = text.Insert(pct + 1, " (3.3.1: " + value + ")");
        Debug.Log("[SpectreReplay] 结算跨口径已内联：(3.3.1: " + value + ")");
    }
}
