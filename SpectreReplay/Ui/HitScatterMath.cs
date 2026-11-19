using System;
using System.Collections.Generic;

namespace SpectreReplay.Ui
{
    internal static class HitScatterMath
    {
        /// <summary>着色档位（与贴图上的三种语义色一一对应）。</summary>
        internal enum Band
        {
            /// <summary>落在较紧的窗口内（通常是 Perfect）。</summary>
            Best = 0,
            /// <summary>落在较宽的窗口内、但超出较紧的那个（通常是 Pure）。</summary>
            Middle = 1,
            /// <summary>两个窗口都没进。</summary>
            Worst = 2,
        }

        /// <summary>拿不到窗口数据时的兜底估算值（原版常识：25ms / 60ms）。</summary>
        internal const float FallbackTightMs = 25f;
        internal const float FallbackWideMs = 60f;

        /// <summary>
        /// 归一化到「较紧窗口的倍数」。1.0 = 刚好卡在边界。
        /// </summary>
        internal static float Ratio(float absMs, float tightMs)
        {
            if (tightMs <= 0f) tightMs = FallbackTightMs;
            return absMs / tightMs;
        }

        /// <summary>
        /// 柱高（像素）。满量程 = <paramref name="fullScale"/> 倍紧窗口。
        /// 永不为 0——0 高的柱子在图上等于不存在，而「偏差极小」和「这一击没画」
        /// 在玩家眼里是同一件事。
        /// </summary>
        internal static int BarHeight(float absMs, float tightMs, int halfHeight, float fullScale)
        {
            if (halfHeight <= 0) return 0;
            if (fullScale <= 0f) fullScale = 2f;
            float r = Ratio(absMs, tightMs) / fullScale;
            if (r < 0f) r = 0f;
            if (r > 1f) r = 1f;
            int h = (int)(halfHeight * r);
            return h < 1 ? 1 : h;
        }

        /// <summary>
        /// 两档窗口的紧/宽。**Perfect 恒宽于 Pure**（45° vs 30° 上限），
        /// 但这里按数值取 min/max，不依赖那个顺序假设。
        /// 任一为非正（缺数据）就整对退回兜底值——只补一半会让宽窄关系失真。
        /// </summary>
        internal static void Windows(float perfectMs, float pureMs,
            out float tightMs, out float wideMs)
        {
            if (perfectMs > 0f && pureMs > 0f)
            {
                tightMs = Math.Min(perfectMs, pureMs);
                wideMs = Math.Max(perfectMs, pureMs);
                return;
            }
            tightMs = FallbackTightMs;
            wideMs = FallbackWideMs;
        }

        /// <summary>
        /// 该偏差属于哪一档。**这三档必须都可达**——原实现里「中等」这一支
        /// 是死代码，整张图只剩绿和红，于是「偏移 160ms 还全是绿的」。
        /// </summary>
        internal static Band Classify(float absMs, float tightMs, float wideMs)
        {
            if (absMs <= tightMs) return Band.Best;
            if (absMs <= wideMs) return Band.Middle;
            return Band.Worst;
        }

        /// <summary>
        /// 一格里该画的样本下标：**取该格内 |ms| 最大的那个**。
        ///
        /// <para>抽稀的任务是「别把异常藏起来」，取第一个/取平均都正好相反。
        /// 原实现取第一个 ⇒ 1499 击只画 500 根，统计里的 -166.4ms 恰好不在
        /// 所在格的首位，图上就找不到它。</para>
        ///
        /// <returns>下标；<paramref name="from"/> 越界时返回 -1。</returns>
        /// </summary>
        internal static int PickExtreme(float[] samples, int from, int to)
        {
            if (samples == null || from < 0 || from >= samples.Length) return -1;
            if (to > samples.Length) to = samples.Length;
            if (to <= from) return -1;
            int best = from;
            float bestAbs = Math.Abs(samples[from]);
            for (int i = from + 1; i < to; i++)
            {
                float a = Math.Abs(samples[i]);
                if (a > bestAbs)
                {
                    bestAbs = a;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>指纹抽样步长上限：每列最多看 64 个点，一帧一次不会成为热点。</summary>
        private const int FingerprintStride = 64;

        /// <summary>
        /// 命中图缓存指纹：**样本值与两档窗口一起**进哈希。
        ///
        /// <para><b>为什么不能只比样本条数。</b>同一个 <c>ReplayData</c>、同样的条数，
        /// 换难度 / 换倍速 / 换关卡之后窗口与毫秒都会变（窗口随 pitch 与 bpm×speed，
        /// 误差值随 1/pitch），而判据只看条数就会一直拿旧贴图：设置页的图"不会变化"，
        /// 游戏里的判定却在变——2026-11-19 用户报的形态。条数只是最粗的一道。</para>
        ///
        /// <para>抽样而不是全量：这几张表挂在每帧重绘的 IMGUI 页上，几千条逐个哈希
        /// 是白烧 CPU；而窗口/误差在同一份回放里是分段常量（按砖的 speed），
        /// 64 点等距抽样足以捕捉任何一次整体换算变化。碰撞只表现为"该重建时用了
        /// 旧图"，与原来的行为持平，不会更糟。</para>
        /// </summary>
        internal static int Fingerprint(IList<float> samples, IList<float> perfectWins,
            IList<float> pureWins)
        {
            unchecked
            {
                uint h = 2166136261u;   // FNV-1a 偏移基
                h = MixSeries(h, samples);
                h = MixSeries(h, perfectWins);
                h = MixSeries(h, pureWins);
                return (int)h;
            }
        }

        private static uint MixSeries(uint h, IList<float> values)
        {
            const uint prime = 16777619u;
            if (values == null)
                return h * prime;
            int n = values.Count;
            h = (h ^ (uint)n) * prime;
            if (n == 0)
                return h;
            int stride = Math.Max(1, n / FingerprintStride);
            for (int i = 0; i < n; i += stride)
                h = (h ^ unchecked((uint)values[i].GetHashCode())) * prime;
            // 末尾那个点必须看：窗口是分段常量，最后一段的宽度只在这里露头。
            return (h ^ unchecked((uint)values[n - 1].GetHashCode())) * prime;
        }
    }
}
