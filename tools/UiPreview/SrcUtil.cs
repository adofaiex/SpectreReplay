// 源码级检查的共用小工具。
//
// 为什么单独抽出来：**StripComments 已经被逐字抄了三份**
// （PatchGuardCheck / FrameSourceCheck / HitTrackCheck）。
// 抄到第四份时就该停了——同一段判据存在两个副本时，改了一份忘了另一份，
// 就会出现"检查 A 变红、检查 B 还是绿的"这种最难查的现象，
// 而闸门看起来还是全绿。
//
// 用法：读进来先 Clean 掉注释，再往**控制流**上落判据。
// 注释必须先剥，否则「文件里出现过 return false」这种判据会被
// 说明文字里的例子骗过去——第一版 Guard 检查就是这么全绿的。
using System;
using System.IO;
using System.Text;

namespace UiPreview
{
    internal static class SrcUtil
    {
        /// <summary>
        /// 游戏程序集所在目录（<c>adofai-libs</c>）。
        ///
        /// 本地布局是**仓库的同级目录**（csproj 里 31 个 HintPath 都写
        /// <c>..\..\adofai-libs\</c>）；CI 里由检出位置决定，靠环境变量
        /// <c>ADOFAI_LIBS</c> 指过去。两种布局都要能跑——否则依赖真 dll 的
        /// 那几项检查只会在本地绿、在 CI 里静默跳过，而「绿着的闸门其实有几项
        /// 没跑」正是这套工具最想避免的形态。
        /// </summary>
        internal static string GameLibsRoot()
            => ResolveRoot("ADOFAI_LIBS", "adofai-libs", @"D:\Projects\adofai-libs");

        /// <summary>游戏程序集的完整路径；目录不存在返回 null（调用方决定跳过还是判红）。</summary>
        internal static string GameAssembly(string fileName)
        {
            string libs = GameLibsRoot();
            return libs == null ? null : Path.Combine(libs, fileName);
        }

        /// <summary>
        /// 按 ① 环境变量 ② 从产物目录逐级向上找同级目录 ③ 历史绝对路径 定位。
        ///
        /// **为什么要有 ②。** 本地产物在
        /// <c>&lt;repo&gt;\tools\UiPreview\bin\Release\net10.0-windows</c>，
        /// CI 在 <c>&lt;ws&gt;\SpectreReplay\tools\UiPreview\bin\Release\net10.0-windows</c>：
        /// 两者往上若干层都会经过自己的仓库根，再上一层就是依赖仓库的所在。
        /// 写死绝对路径则 CI 必挂；只认环境变量则本地每次都要额外配置。
        /// </summary>
        private static string ResolveRoot(string envName, string sibling, string legacy)
        {
            string fromEnv = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
                return fromEnv;

            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                string probe = Path.Combine(dir, sibling);
                if (Directory.Exists(probe)) return probe;
                DirectoryInfo parent = Directory.GetParent(
                    dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (parent == null) break;
                dir = parent.FullName;
            }

            return Directory.Exists(legacy) ? legacy : null;
        }

        /// <summary>按相对路径读源码；读不到返回空串（由各检查自己判）。</summary>
        internal static string Read(string projectRoot, string relative)
        {
            if (IsPagesSelector(relative)) return ReadPages(projectRoot);
            string p = Path.Combine(projectRoot, relative.Replace('/', '\\'));
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }

        /// <summary>剥掉 // 与 /* */ 注释（正确跳过字符串与字符字面量）。</summary>
        internal static string Clean(string src)
        {
            if (src.Length == 0)
            {
                return src;
            }
            var sb = new StringBuilder(src.Length);
            bool inBlock = false, inStr = false, inChar = false, esc = false;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (inBlock)
                {
                    if (c == '*' && i + 1 < src.Length && src[i + 1] == '/')
                    { inBlock = false; i++; }
                    else if (c == '\n') sb.Append(c);
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '/')
                {
                    while (i < src.Length && src[i] != '\n') i++;
                    sb.Append('\n');
                    continue;
                }
                if (!inStr && !inChar && c == '/' && i + 1 < src.Length
                    && src[i + 1] == '*')
                { inBlock = true; i++; continue; }
                if (esc) { esc = false; sb.Append(c); continue; }
                if (c == '\\') { esc = true; sb.Append(c); continue; }
                // **字符串内的单引号不是字符字面量的定界符。**
                // 与 HardTextCheck 曾栽的是同一个坑：撇号（英文日志文案里极常见，
                // 如 "the game's UpdateHoldKeys"）若在 inStr 期间也切换 inChar，
                // 会让之后整个文件的注释都因 `!inChar` 守卫而剥不掉，
                // 于是"不许出现 X"那类判据把注释里的说明文字当成缺陷（常红）。
                if (!inChar && c == '"') { inStr = !inStr; sb.Append(c); continue; }
                if (inStr) { sb.Append(c); continue; }
                if (c == '\'') inChar = !inChar;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>读 + 剥注释，一步到位。</summary>
        internal static string Source(string projectRoot, string relative)
            => Clean(Read(projectRoot, relative));

        /// <summary>
        /// 页面源码的**逻辑路径**：拆分后一个逻辑页面对应 `Pages\*.cs` 多个文件。
        ///
        /// 2026-11-18 把 `Pages/Pages.cs`（3371 行）按职责拆成六个 partial 文件，
        /// 但闸门里有一百多条判据是按「某个符号出现在页面源码里」写的。若让它们
        /// 继续只读 `Pages.cs`，拆分当天它们会**全部变红**——而如果改判据去读
        /// 某一个新文件，剩下的页面文件就永久逃出闸门，那是把纪律换成了通过率。
        ///
        /// 所以保留这个逻辑路径不变：凡按它读取的地方，拿到的是**全部页面文件
        /// 按固定顺序拼接**的结果。判据一个字都不用改，覆盖面反而从「一个文件」
        /// 变成「所有页面文件」。
        /// </summary>
        internal const string PagesSelector = "Pages/Pages.cs";

        /// <summary>该相对路径是不是「页面源码集合」这个逻辑路径。</summary>
        internal static bool IsPagesSelector(string relative)
        {
            return relative != null
                && relative.Replace('\\', '/').Equals(PagesSelector, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 页面目录下的全部 <c>partial class Pages</c> 源文件（完整路径），
        /// <c>Pages.cs</c> 在前、其余按名字序（共 7 个文件：1 壳 + 6 页）。
        ///
        /// **排除独立类 <c>ReplayInfoPanel.cs</c>**：它一直是个独立类文件，拆分前
        /// 就不在 <c>Pages.cs</c> 内；混入 <c>Pages\Pages.cs</c> 逻辑路径不仅会扩大
        /// 判据扫描口径（带来潜在假绿），还会让变异脚本无故重写它。文案检查另由
        /// <see cref="LocCheck"/> 显式纳入它。
        ///
        /// **顺序必须确定**：多条判据靠「两个符号的相对距离」判接线（例如
        /// `IndexOf(marker)` 之后 240 字符内必须有 gate），顺序随机的拼接
        /// 会让同一次运行时而绿时而红。
        /// </summary>
        internal static string[] PageFiles(string projectRoot)
        {
            string dir = Path.Combine(projectRoot, "Pages");
            if (!Directory.Exists(dir)) return new string[0];
            var files = new System.Collections.Generic.List<string>();
            string shell = Path.Combine(dir, "Pages.cs");
            if (File.Exists(shell)) files.Add(shell);
            var rest = new System.Collections.Generic.List<string>(Directory.GetFiles(dir, "*.cs"));
            rest.Sort(StringComparer.Ordinal);
            foreach (string f in rest)
            {
                if (string.Equals(f, shell, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFileName(f), "ReplayInfoPanel.cs", StringComparison.OrdinalIgnoreCase)) continue;
                files.Add(f);
            }
            return files.ToArray();
        }

        /// <summary>
        /// 把页面文件拼成一份源码文本，每份前面留一行边界注释。
        ///
        /// 边界注释不是装饰：判据变红时要能说清是**哪个**文件里出的问题，
        /// 否则面对一份三千多行的拼接体只能靠猜。
        /// </summary>
        private static string JoinPages(string projectRoot)
        {
            var sb = new StringBuilder();
            foreach (string f in PageFiles(projectRoot))
            {
                sb.Append("// >>> page: ").Append(Path.GetFileName(f)).Append('\n');
                sb.Append(File.ReadAllText(f));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>页面源码拼接（未剥注释）。</summary>
        internal static string ReadPages(string projectRoot) => JoinPages(projectRoot);

        /// <summary>页面源码拼接（已剥注释）。</summary>
        internal static string SourcePages(string projectRoot) => Clean(JoinPages(projectRoot));
    }
}