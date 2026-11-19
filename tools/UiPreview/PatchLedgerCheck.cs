// 补丁失败账本必须随装配复位。
//
// 缺陷史（2026-11-18 审计 P1-2）：SpectrePatchHub 的 ApplyFailures /
// failureSummaryShown / _jalibProbed 三个字段是**跨会话**的状态，而
// Initialize() 与 UnpatchAll() 都不清它们。后果：
//   · 关开一次模组后，设置页常驻的 ui.patchfailed (N) 会继续显示上一次会话
//     的失败项 —— 而那一行是「补丁没挂上」唯一的常驻信号（铁律①）。
//   · 新出现的失败不再弹汇总通知（failureSummaryShown 还是 true）。
//   · _jalibProbed 让 JALib 补丁集只探测一次，重装后不再自愈。
//
// 判据落在「Initialize 方法体里必须有这三句复位」，因为缺陷的成因正是
// 「装配时没有复位」，而不是别处写错了什么。
using System;

namespace UiPreview
{
    internal static class PatchLedgerCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 补丁失败账本复位 ──");
            string hub = SrcUtil.Source(projectRoot, "Patch/Shared/SpectrePatchHub.cs");
            int fail = 0;
            void Expect(bool ok, string msg)
            {
                if (ok) return;
                Console.WriteLine("    ✗ " + msg);
                fail++;
            }

            int at = hub.IndexOf("public static void Initialize(Assembly assembly)", StringComparison.Ordinal);
            Expect(at >= 0, "找不到 SpectrePatchHub.Initialize（装配入口被挪走/改名了？）");
            if (at >= 0)
            {
                string body = MethodBody(hub, at);
                Expect(body.Contains("ApplyFailures.Clear();", StringComparison.Ordinal),
                    "① Initialize 没清 ApplyFailures —— 设置页会继续显示上一次会话的补丁失败项");
                Expect(body.Contains("failureSummaryShown = false;", StringComparison.Ordinal),
                    "② Initialize 没复位 failureSummaryShown —— 新出现的失败不再弹汇总通知");
                Expect(body.Contains("_jalibProbed = false;", StringComparison.Ordinal),
                    "③ Initialize 没复位 _jalibProbed —— 重装后不再重新探测 JALib 补丁集");
            }

            if (fail == 0)
                Console.WriteLine("    ✓ Initialize 复位失败账本 / 汇总标记 / JALib 探测");
            return fail;
        }

        /// <summary>取 signatureAt 之后第一个方法体（花括号配对，已剥注释所以串内花括号不影响）。</summary>
        private static string MethodBody(string src, int signatureAt)
        {
            int open = src.IndexOf('{', signatureAt);
            if (open < 0) return "";
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}')
                {
                    depth--;
                    if (depth == 0) return src.Substring(open, i - open + 1);
                }
            }
            return src.Substring(open);
        }
    }
}
