// 回放命中推进兜底的纯数学判据（2026-11-19，Atohs Strict 卡死实测后加的）。
//
// 缺陷形态：回放不是"重放按键"而是"拿录制角度重判"（ReplayHit 传 null hitTick，
// 走 scrPlanet.SwitchChosen 的角度分支），而录制那一击的判定来自音频 tick（时间分支）。
// 两侧窗口 = max(角度下限 60°/45°/30°, 时间下限换算的角度)：Normal 0.065s、Strict
// 0.040s。有效 BPM 高的段落（SetSpeed 到 780/975 很常见）Normal 的计入窗口被时间项
// 撑到 90° 以上、Strict 还停在 93.6° 附近——同一击于是"Normal 计入、Strict 不计入"，
// 砖块不推进，泵的条件"当前砖 == 快照砖"再也不会成立：行星原地打转、曲子照放、
// 回放不动（实机 Player.log 里那句「判定模式自动对齐: Normal → Strict」之后
// 再也没有 replay finished）。
//
// 这一组判据钉的是兜底的三个前提，任何一条错都会把"卡死"换成"判定被篡改"：
//   ① 档位 → 窗口：None/PerfectsOnly/PurePerfectOnly 各要求 Counted/Perfect/Pure；
//   ② 只在"数据说这一击推进了砖块 + 游戏没有强制计入 + 角度确实在窗口外"时才收；
//   ③ 收进去的角度必须留在窗口内、贴边、且保持录制时的偏早/偏晚方向。
using System;
using SpectreReplay.Features.Replay;

namespace UiPreview
{
    internal static class HitAdvanceGuardCheck
    {
        // 780 BPM（195 × SetSpeed 4）时的真实窗口，取自 scrMisc.GetAdjustedAngleBoundaryInDeg：
        //   Normal  0.065s → 152.1°    Strict  0.040s → 93.6°
        // 实测那一击的录制角度是 -93.6°：Normal 收、Strict 不收，正是卡死点。
        private const double CountedStrict780 = 93.6;
        private const double CountedNormal780 = 152.1;
        private const double Perfect780 = 70.2;
        private const double Pure780 = 58.5;

        internal static int Run()
        {
            Console.WriteLine("  ── 回放命中推进兜底（HitAdvanceGuard）──");
            int fail = 0;

            // ① 档位 → 要求窗口
            double bNone = HitAdvanceGuard.RequiredBoundaryDeg(0, CountedNormal780, Perfect780, Pure780);
            double bPerfects = HitAdvanceGuard.RequiredBoundaryDeg(1, CountedNormal780, Perfect780, Pure780);
            double bPure = HitAdvanceGuard.RequiredBoundaryDeg(2, CountedNormal780, Perfect780, Pure780);
            double bBogus = HitAdvanceGuard.RequiredBoundaryDeg(99, CountedNormal780, Perfect780, Pure780);
            if (bNone == CountedNormal780 && bPerfects == Perfect780 && bPure == Pure780
                && bBogus == CountedNormal780)
            {
                Console.WriteLine($"    ✓ ① 档位窗口：None={bNone}°/PerfectsOnly={bPerfects}°/PurePerfectOnly={bPure}°（越界档位退回 None）");
            }
            else
            {
                Console.WriteLine($"    ✗ ① 档位窗口算错：None={bNone} PerfectsOnly={bPerfects} Pure={bPure} bogus={bBogus}");
                fail++;
            }

            // ② 何时才收：四个条件缺一不可
            double outsideStrict = -93.6 * HitAdvanceGuard.DegToRad;
            double insideNormal = -93.6 * HitAdvanceGuard.DegToRad;
            bool[] got =
            [
                HitAdvanceGuard.TryClampCurrAngle(outsideStrict, CountedStrict780, false, true, out _),   // 该收
                HitAdvanceGuard.TryClampCurrAngle(outsideStrict, CountedNormal780, false, true, out _),  // 窗口够宽 → 不收
                HitAdvanceGuard.TryClampCurrAngle(outsideStrict, CountedStrict780, true, true, out _),   // 强制计入 → 不收
                HitAdvanceGuard.TryClampCurrAngle(outsideStrict, CountedStrict780, false, false, out _), // 数据说没推进 → 不收
                HitAdvanceGuard.TryClampCurrAngle(outsideStrict, 0.0, false, true, out _),               // 窗口未知 → 不猜
                HitAdvanceGuard.TryClampCurrAngle(insideNormal * 0.2, CountedStrict780, false, true, out _), // 窗口内 → 不收
            ];
            bool[] want = [true, false, false, false, false, false];
            if (Same(got, want))
            {
                Console.WriteLine("    ✓ ② 只在「窗口外 + 数据说推进了 + 无强制计入标志」时收角度（窗口内/窗口未知/没推进都不动）");
            }
            else
            {
                Console.WriteLine("    ✗ ② 收角度条件判错：" + string.Join(",", got) + "（期望 " + string.Join(",", want) + "）");
                fail++;
            }

            // ③ 收进去的角度：留在窗口内、贴边、方向不变
            HitAdvanceGuard.TryClampCurrAngle(outsideStrict, CountedStrict780, false, true, out double clampedEarly);
            HitAdvanceGuard.TryClampCurrAngle(-outsideStrict, CountedStrict780, false, true, out double clampedLate);
            double earlyDeg = clampedEarly * HitAdvanceGuard.RadToDeg;
            double lateDeg = clampedLate * HitAdvanceGuard.RadToDeg;
            bool earlyOk = earlyDeg < 0.0 && Math.Abs(earlyDeg) < CountedStrict780
                && Math.Abs(earlyDeg) > CountedStrict780 - 1.0;
            bool lateOk = lateDeg > 0.0 && Math.Abs(lateDeg) < CountedStrict780
                && Math.Abs(lateDeg) > CountedStrict780 - 1.0;
            bool notFakedPerfect = Math.Abs(earlyDeg) > CountedStrict780 * 0.5;
            if (earlyOk && lateOk && notFakedPerfect)
            {
                Console.WriteLine($"    ✓ ③ 收进窗口边缘且保留方向：偏早 {earlyDeg:F2}° / 偏晚 {lateDeg:F2}°（未伪造为 0 = XPerfect，窗口 {CountedStrict780}°）");
            }
            else
            {
                Console.WriteLine($"    ✗ ③ 收角度形态错：偏早 {earlyDeg:F2}° 偏晚 {lateDeg:F2}°（窗口 {CountedStrict780}°，要求同侧且贴边）");
                fail++;
            }

            // ④ 极小窗口退化：不许收到 0（0 = XPerfect，等于伪造完美命中）
            HitAdvanceGuard.TryClampCurrAngle(-1.0, HitAdvanceGuard.EdgeMarginDeg * 0.5, false, true, out double tiny);
            double tinyDeg = tiny * HitAdvanceGuard.RadToDeg;
            if (tinyDeg < 0.0 && Math.Abs(tinyDeg) > 0.0)
            {
                Console.WriteLine($"    ✓ ④ 窗口小于余量时退到半窗口（{tinyDeg:F5}°），不会退化成 0 = 伪造 XPerfect");
            }
            else
            {
                Console.WriteLine($"    ✗ ④ 极小窗口把角度收到了 0 或翻向：{tinyDeg:F5}°");
                fail++;
            }

            return fail;
        }

        private static bool Same(bool[] a, bool[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
