// info.json 的 GameVersion 必须与 AGENT.md 的「游戏基线」一致。
//
// 为什么值得单独一条（2026-11-18 审计 P1-4）：UMM 用 info.json 的 GameVersion
// 做版本匹配与提示。基线是 3.4.0 / r150（AGENT.md §1 硬约束表），而 info.json
// 长期写着 3.3.1 —— 于是 3.4.0 玩家在 UMM 里看到「版本不符」，而构建、闸门、
// 断言全绿：没有任何一项在看这个字段。判据把「文档基线」与「模组声明」绑在
// 一起，任何一边漂移都会红。
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace UiPreview
{
    internal static class VersionBaselineCheck
    {
        internal static int Run(string projectRoot)
        {
            Console.WriteLine("  ── 版本基线一致性（info.json ↔ AGENT.md） ──");
            int fail = 0;
            void Expect(bool ok, string msg)
            {
                if (ok) return;
                Console.WriteLine("    ✗ " + msg);
                fail++;
            }

            // ① info.json 的 GameVersion
            string infoPath = Path.Combine(projectRoot, "info.json");
            Expect(File.Exists(infoPath), "找不到 " + infoPath);
            if (!File.Exists(infoPath)) return fail;
            string declared = ReadJsonStringField(File.ReadAllText(infoPath), "GameVersion");
            Expect(declared != null, "info.json 里没有 GameVersion 字段");
            if (declared == null) return fail;

            // ② AGENT.md §1 硬约束表里的「游戏基线」行
            string agentPath = Path.Combine(Directory.GetParent(projectRoot).FullName, "AGENT.md");
            Expect(File.Exists(agentPath), "找不到 " + agentPath);
            if (!File.Exists(agentPath)) return fail;

            string baseline = null;
            foreach (string line in File.ReadAllLines(agentPath))
            {
                if (line.IndexOf("游戏基线", StringComparison.Ordinal) < 0) continue;
                // 该行形如：表格行「游戏基线」里写着 ADOFAI 的 3.4.0（build r150）
                // 与 Unity 的 6000.3.25f1。取第一个 x.y.z —— 3.4.0 必须在 6000.3.25 之前。
                var m = Regex.Match(line, @"\d+\.\d+\.\d+");
                if (m.Success) baseline = m.Value;
                break;
            }
            Expect(baseline != null, "AGENT.md 里找不到「游戏基线」行或其中的版本号");
            if (baseline == null) return fail;

            Expect(string.Equals(declared, baseline, StringComparison.Ordinal),
                "info.json GameVersion=" + declared + "，AGENT.md 游戏基线=" + baseline
                + " —— UMM 按声明版本做匹配/提示，两边必须一致");

            if (fail == 0)
                Console.WriteLine("    ✓ info.json GameVersion = " + declared + "（与 AGENT.md 基线一致）");
            return fail;
        }

        /// <summary>
        /// 从 JSON 文本里取顶层字符串字段（只认 "name": "value" 形状）。
        /// 不用正则：这个文件是手写的元数据，字段就那几个，按行扫比维护转义更省心。
        /// </summary>
        private static string ReadJsonStringField(string json, string name)
        {
            foreach (string line in json.Split('\n'))
            {
                int at = line.IndexOf(name, StringComparison.Ordinal);
                if (at < 0) continue;
                int colon = line.IndexOf(':', at);
                if (colon < 0) continue;
                int q1 = line.IndexOf('"', colon);
                if (q1 < 0) continue;
                int q2 = line.IndexOf('"', q1 + 1);
                if (q2 < 0) continue;
                return line.Substring(q1 + 1, q2 - q1 - 1);
            }
            return null;
        }
    }
}
