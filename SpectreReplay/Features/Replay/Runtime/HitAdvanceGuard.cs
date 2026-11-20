using System;

namespace SpectreReplay.Features.Replay;

/// <summary>
/// 回放命中推进的兜底：**录制里成立的一击，回放里必须也成立**。
///
/// <para><b>为什么需要它。</b>回放的每一击不是"重放按键"，而是"拿录制时的角度重判"
/// （<see cref="ReplayPlayer"/> 的 ReplayHit 刻意传 <c>null</c> hitTick，让
/// <c>scrPlanet.SwitchChosen</c> 走角度分支）。录制侧的判定来自音频 tick（时间分支），
/// 两侧窗口只在高倍速时才分叉：窗口半宽 = max(角度下限, 时间下限换算的角度)，
/// 角度下限是 60°/45°/30° 的常量，时间下限 Normal 0.065s、Strict 0.040s。有效 BPM
/// 一旦超过 ~307（谱面 SetSpeed 很容易到 780、975），Normal 的计入窗口被时间项撑到
/// 90° 以上，Strict 还停在 60° 起的角度下限——同一击就会"Normal 计入、Strict 不计入"。
/// 2026-11-19 实测：Atohs（BPM 195 + SetSpeed），floor 946 有效 780 BPM、录制角度
/// -93.6°，Normal 的 ±152.1° 收、Strict 的 ±93.56° 不收。</para>
///
/// <para><b>为什么"不收"就等于永久卡死。</b>泵的推进条件是"当前砖 == 快照砖"
/// （<c>Patch_Playback</c> 的 while）。这一击不推进砖块，而下一条快照记在下一块砖上，
/// 条件再也不会成立：行星原地打转、曲子照放、回放不动。游戏的 noFail 兜底
/// （<c>Die()</c> 里 <c>noFailInfiniteMargin = true; Hit(null)</c>）在回放期被
/// HitLocked 挡掉，自愈路径不存在——所以必须在这里把结论钉成录制的那个。</para>
///
/// <para><b>兜底形态。</b>录制已经用"下一条快照在当前砖之后"证明了这一击推进了砖块，
/// 于是把它收进"游戏一定会计入"的窗口边缘，保留录制时的偏早/偏晚方向。收进边缘
/// 而不是收到 0：显示出来仍是擦边过的 VeryEarly/EarlyPerfect/PerfectMinus（与录制
/// 那一击同级），不会把一次擦边命中画成 XPerfect 骗人。</para>
///
/// <para>本类不碰 UnityEngine，判定只用纯算术——tools/UiPreview 因此能链接真源码
/// 跑断言（HitAdvanceGuardCheck），而不是在闸门里复刻一遍公式。</para>
/// </summary>
internal static class HitAdvanceGuard
{
    // 最后一击没有下一张快照，录制终点仍可证明它推进过砖块。
    internal static bool ProvesAdvance(int currentFloor, int? nextFloor, int? endTile)
        => nextFloor.HasValue ? nextFloor.Value > currentFloor
            : endTile.HasValue && endTile.Value > currentFloor;

    /// <summary>
    /// 收进窗口后留的余量（度）。<c>scrMisc.SelectHitMarginByTimeBoundary</c> 的比较是
    /// 严格不等号（<c>target &gt; -boundary</c>），贴边会落在窗口外。
    /// </summary>
    internal const double EdgeMarginDeg = 0.01;

    /// <summary>弧度 → 度。取游戏侧同一个字面值（float 精度的 π），别换成 Math.PI。</summary>
    internal const double RadToDeg = 57.295780181884766;

    /// <summary>度 → 弧度。同上，与 <c>scrMisc</c> 里的 0.01745329238474369 同源。</summary>
    internal const double DegToRad = 0.01745329238474369;

    /// <summary>
    /// 录制档位（<c>HitMarginLimit</c>）要求的最低窗口半宽（度）。
    ///
    /// <para><b>为什么不是恒取 Counted。</b>档位会抬高门槛：
    /// <c>None</c> 只要求"计入"（窗口 = Counted，接受 VeryEarly/VeryLate）；
    /// <c>PerfectsOnly</c> 要求 Perfect 档（EarlyPerfect/LatePerfect 才算）；
    /// <c>PurePerfectOnly</c> 要求 Pure 档。<paramref name="limit"/> 用 int 而不是
    /// 游戏的枚举：本类要能被离线闸门编译（闸门不链游戏程序集），枚举顺序在
    /// <c>HitMarginLimit</c> 里是 None/PerfectsOnly/PurePerfectOnly，别换序。</para>
    /// </summary>
    internal static double RequiredBoundaryDeg(int limit, double countedDeg, double perfectDeg, double pureDeg)
        => limit switch
        {
            1 => perfectDeg,
            2 => pureDeg,
            _ => countedDeg,
        };

    /// <summary>
    /// 这一击需不需要把角度收进窗口。
    ///
    /// <para>四个条件缺一不可：数据说明这一击推进了砖块（<paramref name="nextAdvances"/>）、
    /// 游戏没有"无条件计入"的强制标志（<paramref name="forceCounted"/>：midspin /
    /// noFail 兜底命中 / 下一块砖是 auto）、窗口已知、以及录制角度确实落在窗口外。
    /// 只收"本来就不计入"的那些——正常情况下角度在窗口内，这条路径一次都不触发，
    /// 回放行为与以前逐位一致。</para>
    /// </summary>
    internal static bool NeedsClamp(double currAngleRad, double boundaryDeg, bool forceCounted, bool nextAdvances)
    {
        if (!nextAdvances || forceCounted) return false;
        if (!(boundaryDeg > 0.0)) return false;
        return Math.Abs(currAngleRad) * RadToDeg >= boundaryDeg;
    }

    /// <summary>
    /// 把录制角度收进窗口边缘（保留偏早/偏晚方向）。返回值仍是 <c>CurrAngle</c> 语义：
    /// <c>(angle - targetExitAngle) * dir</c>，弧度。
    ///
    /// <para>窗口窄到连余量都放不下（极小窗口）时退到半窗口，而不是 0——
    /// 0 会把这一击判成 XPerfect，等于伪造一个"完美命中"。</para>
    /// </summary>
    internal static double ClampCurrAngle(double currAngleRad, double boundaryDeg)
    {
        double edgeDeg = boundaryDeg - EdgeMarginDeg;
        if (edgeDeg <= 0.0) edgeDeg = boundaryDeg * 0.5;
        return (currAngleRad < 0.0 ? -1.0 : 1.0) * edgeDeg * DegToRad;
    }

    /// <summary>
    /// 一次性入口：需要收角度时给出替代的 <c>CurrAngle</c>（弧度），否则返回 false
    /// 并原样带回输入值。调用方据此决定要不要改写这一击的星球角度。
    /// </summary>
    internal static bool TryClampCurrAngle(double currAngleRad, double boundaryDeg, bool forceCounted,
        bool nextAdvances, out double clampedCurrAngleRad)
    {
        clampedCurrAngleRad = currAngleRad;
        if (!NeedsClamp(currAngleRad, boundaryDeg, forceCounted, nextAdvances))
            return false;
        clampedCurrAngleRad = ClampCurrAngle(currAngleRad, boundaryDeg);
        return true;
    }
}
