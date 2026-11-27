using System;
using System.IO;

namespace SpectreReplay.Features.EffectRemover;

/// <summary>同目录临时文件发布：失败不截断旧预设，补拉不留下阻碍重试的半文件。</summary>
internal static class PresetFile
{
    internal static void Write(string path, byte[] data, bool overwrite)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            if (overwrite && File.Exists(path))
                File.Replace(temporary, path, null);
            else
                // File.Move 不覆盖已存在目标，也能挡住下载期间出现同名本地文件的竞态。
                File.Move(temporary, path);
        }
        finally
        {
            // 不采用「删旧再移动」回退；替换不可用应报失败并保留原目标。
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
