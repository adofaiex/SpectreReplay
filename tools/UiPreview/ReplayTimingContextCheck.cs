// 设置页分布图缓存判据。
using System;
using System.Collections.Generic;
using SpectreReplay.Ui;

namespace UiPreview
{
    internal static class ReplayTimingContextCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 回放设置页分布图缓存──");
            int fail = 0;

            // ① 分布图缓存：判据必须含指纹（只比"同一份 data + 同样条数"会一直画旧图）
            string scatter = SrcUtil.Source(projectRoot, "Ui/HitScatter.cs");
            bool cached = scatter.Contains("HitScatterMath.Fingerprint(rep.SamplesMs",
                    StringComparison.Ordinal)
                && scatter.Contains("_builtFingerprint == fingerprint", StringComparison.Ordinal);
            Report(ref fail, cached, "① 分布图缓存判据含样本+窗口指纹，不只是条数");

            // ② 卸载回放必须丢图（否则下一份回放沿用旧贴图/旧上下文）
            string state = SrcUtil.Source(projectRoot, "State/SpectreState.cs");
            Report(ref fail,
                state.Contains("Ui.HitScatter.Reset();", StringComparison.Ordinal),
                "② 卸载回放时清掉分布图缓存");

            // ③ 指纹本身：同样数据同值；误差、窗口、条数任一变化都必须换值
            var samples = new List<float> { -3.5f, 0.2f, 40f, 12f };
            var perfect = new List<float> { 25f, 25f, 60f, 60f };
            var pure = new List<float> { 20f, 20f, 40f, 40f };
            int baseline = HitScatterMath.Fingerprint(samples, perfect, pure);
            bool sameAgain = HitScatterMath.Fingerprint(samples, perfect, pure) == baseline;
            var samplesB = new List<float>(samples) { 1f };
            bool countChanges = HitScatterMath.Fingerprint(samplesB, perfect, pure) != baseline;
            var perfectB = new List<float> { 25f, 25f, 60f, 60f };
            perfectB[3] = 30f;                          // 只有最后一段窗口变：抽样必须看到末端
            bool windowChanges = HitScatterMath.Fingerprint(samples, perfectB, pure) != baseline;
            var samplesC = new List<float>(samples);
            samplesC[2] = 41f;                          // 只有中间一个样本变
            bool sampleChanges = HitScatterMath.Fingerprint(samplesC, perfect, pure) != baseline;
            Report(ref fail, sameAgain && countChanges && windowChanges && sampleChanges,
                "③ 指纹：同值稳定；条数/窗口/样本任一变化都换指纹");

            return fail;
        }

        private static void Report(ref int fail, bool ok, string what, string detail = null)
        {
            if (ok)
            {
                Console.WriteLine("    ✓ " + what + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"));
            }
            else
            {
                Console.WriteLine("    ✗ " + what + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）"));
                fail++;
            }
        }
    }
}
