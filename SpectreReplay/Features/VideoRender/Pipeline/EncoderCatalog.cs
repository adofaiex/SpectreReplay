using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// ffmpeg 已编译进哪些编码器（`-encoders` 的解析缓存）。
    ///
    /// **为什么要单独一层。** 只探 NVENC 时，QSV/AMF/AV1 是否可用只能靠猜；
    /// 逐个起 1 帧探测又会在启动时拖出 9 个进程。先把 `-encoders` 的清单缓存下来，
    /// 只对最终选中的那一个做 preflight，既快又不会静默降级。
    ///
    /// 线程模型：主线程调用 <see cref="Supports"/>；未加载时触发后台加载并返回 false。
    /// 加载线程只写缓存，不碰 Unity API。
    /// </summary>
    internal static class EncoderCatalog
    {
        private const int LoadTimeoutMs = 15_000;
        private const int RetryMs = 30_000;

        private static readonly object Lock = new object();
        private static readonly Dictionary<string, HashSet<string>> Cache =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static readonly HashSet<string> Loading =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> FailTick =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal static bool IsLoaded(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            lock (Lock)
                return Cache.TryGetValue(exe, out HashSet<string> set) && set.Count > 0;
        }

        internal static void EnsureLoaded(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return;
            lock (Lock)
            {
                if (IsLoaded(exe) || Loading.Contains(exe)) return;
                if (FailTick.TryGetValue(exe, out int tick)
                    && (uint)(Environment.TickCount - tick) < (uint)RetryMs)
                    return;
                Loading.Add(exe);
            }

            ThreadPool.QueueUserWorkItem(_ => Load(exe));
        }

        internal static bool Supports(string exe, string encoderName)
        {
            if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(encoderName)) return false;
            lock (Lock)
            {
                if (Cache.TryGetValue(exe, out HashSet<string> set))
                    return set.Contains(encoderName);
            }
            EnsureLoaded(exe);
            return false;
        }

        // 只给后台编码预启动用，OnGUI 永远只走 EnsureLoaded。目录未就绪不能被当成
        // 「没有 AV1」给整场作业定案，失败也必须在开始捕获前明确报告。
        internal static bool WaitUntilLoaded(string exe)
        {
            EnsureLoaded(exe);
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < LoadTimeoutMs + 1000)
            {
                if (IsLoaded(exe)) return true;
                lock (Lock)
                    if (!Loading.Contains(exe)) return false;
                Thread.Sleep(10);
            }
            return IsLoaded(exe);
        }

        private static void Load(string exe)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (Process p = Process.Start(info))
                {
                    if (p != null)
                    {
                        StringBuilder output = new StringBuilder();
                        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                        p.ErrorDataReceived += (_, e) => { };
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(LoadTimeoutMs))
                        {
                            try { p.Kill(); } catch { }
                        }
                        // 超时退出后也必须无参 WaitForExit 排空异步输出：
                        // 否则 OutputDataReceived 可能还差最后一段，解析出截断清单。
                        p.WaitForExit();
                        string text;
                        lock (output) text = output.ToString();
                        ParseEncoders(text, names);
                    }
                }
            }
            catch
            {
                names.Clear();
            }

            lock (Lock)
            {
                Cache[exe] = names;
                Loading.Remove(exe);
                if (names.Count == 0)
                    FailTick[exe] = Environment.TickCount;
            }
        }

        private static void ParseEncoders(string text, HashSet<string> names)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] lines = text.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                // ffmpeg -encoders 的视频行形如 ` V....D libx264 ...`。
                // 只取视频编码器；音频/字幕行不参与。
                if (parts.Length >= 2
                    && parts[0].Length >= 6
                    && parts[0][0] == 'V'
                    && parts[0][2] == '.')
                {
                    names.Add(parts[1]);
                }
            }
        }
    }
}
