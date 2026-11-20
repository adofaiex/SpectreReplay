using System;
using System.IO;

namespace SpectreReplay
{
    /// <summary>
    /// 崩溃安全写入：先写同目录临时文件，再用 <see cref="File.Replace(string,string,string)"/>
    /// 原子替换；旧目标先落到 <c>.rolling.bak</c>。
    ///
    /// **为什么不能直接 File.WriteAllText。** 目标文件在写入过程中被截断；进程崩溃、
    /// 断电或磁盘满时留下的是半个 JSON/半个回放，玩家看到的是「配置全丢了」或
    /// 「回放文件损坏」。临时文件 + 替换把「写坏目标」的窗口缩到替换那一步。
    ///
    /// **为什么保留滚动备份。** `.bak` 留给用户主动恢复前的保全副本，常规
    /// 写盘只能更新 `.rolling.bak`，否则关闭设置窗口就会抹掉恢复前的原配置。
    /// 若替换不可用而走「删旧 + 移动」的回退，回退前会先把旧目标复制成滚动备份，
    /// 这样移动中途失败时还能把旧内容找回来。
    /// </summary>
    internal static class AtomicFile
    {
        internal static void WriteAllText(string path, string contents)
            => Write(path, tmp => File.WriteAllText(tmp, contents));

        internal static void WriteAllBytes(string path, byte[] data)
            => Write(path, tmp => File.WriteAllBytes(tmp, data));

        private static void Write(string path, Action<string> write)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is empty", nameof(path));

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir))
            {
                write(path);
                return;
            }

            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            string bak = path + ".rolling.bak";

            write(tmp);

            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }

            try
            {
                if (File.Exists(bak)) File.Delete(bak);
                File.Replace(tmp, path, bak);
            }
            catch
            {
                // File.Replace 在跨卷/只读目标/旧 Mono 上可能不可用。
                // 回退到「删旧 + 移动」；先把旧目标复制成滚动备份，
                // 这样移动中途失败时还能恢复。
                // 保全失败必须中止：此时原目标仍在，不能为了完成本次写入删掉它。
                File.Copy(path, bak, true);
                try
                {
                    File.Delete(path);
                    File.Move(tmp, path);
                }
                catch
                {
                    try { if (File.Exists(bak)) File.Copy(bak, path, true); } catch { }
                    throw;
                }
            }
            // 失败时保留 tmp：里面是这次完整的新内容，排查时可直接看；
            // 旧目标没有被截断，玩家数据仍然可用。
        }
    }
}
