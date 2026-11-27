using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender;

// 已渲染视频的批量转换：mp4/mkv/webm/mov 互转。
// 快路径优先——mkv 什么都能装、movenc 族（mp4/mov）之间整流 copy，秒级完成；
// 非同族源进 mp4/mov 时音频一律转 AAC（源容器编解码不可知，Opus-in-mp4 播放器兼容性差）；
// webm 目标必须重编码（libvpx-vp9 + Opus）。
// 转换在后台线程执行（VP9 重编码可达分钟级，不能占主线程），状态经 volatile 字段暴露给 UI 轮询。
internal static class VideoConverter
{
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".webm", ".mov" };

    internal sealed class ConvertState
    {
        // volatile：工作线程写、UI 线程读；Running=false 的写读对保证其余字段可见
        internal volatile bool Running = true;
        internal volatile int Total;
        internal volatile int Done;
        internal volatile int Converted;
        internal volatile int Skipped;
        internal volatile int Failed;
        internal Exception Error;
        internal readonly List<string> Messages = new(); // 仅在 Running=false 后由 UI 读取
    }

    internal static ConvertState Start(string dir, VideoEncoder.OutContainer target, string exePath)
    {
        var state = new ConvertState();
        var worker = new Thread(() =>
        {
            try
            {
                Run(dir, target, exePath, state);
            }
            catch (Exception ex)
            {
                state.Error = ex;
                Debug.Log("[SpectreReplay] video convert failed: " + ex);
            }
            finally
            {
                state.Running = false;
            }
        })
        {
            IsBackground = true,
            Name = "Spectre.VideoConvert"
        };
        worker.Start();
        return state;
    }

    private static void Run(string dir, VideoEncoder.OutContainer target, string exePath, ConvertState state)
    {
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(dir)
                .Where(p => VideoExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return; // 目录不可读 → 零文件，UI 以“未找到”提示
        }
        state.Total = files.Length;
        foreach (string path in files)
        {
            try
            {
                ConvertOne(path, dir, target, exePath, state);
            }
            catch (Exception ex)
            {
                state.Failed++;
                state.Messages.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
            state.Done++;
        }
    }

    private static void ConvertOne(string path, string dir, VideoEncoder.OutContainer target,
        string exePath, ConvertState state)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string targetExt = VideoEncoder.ExtensionOf(target);
        if (ext == targetExt)
        {
            state.Skipped++;
            return;
        }
        string baseName = Path.GetFileNameWithoutExtension(path);
        string targetPath = Path.Combine(dir, baseName + targetExt);
        if (File.Exists(targetPath))
        {
            state.Skipped++;
            state.Messages.Add(baseName + targetExt + " ← " + ext + ": exists");
            return;
        }
        // 每次尝试都写唯一 partial 文件，成功后才移动到目标；失败不会留下一个
        // 下次被 exists 误判的半成品，也不会覆盖用户已有目标。
        string partialPath = Path.Combine(dir, baseName + ".partial." + Guid.NewGuid().ToString("N") + targetExt);

        string videoArgs;
        string audioArgs;
        switch (target)
        {
            case VideoEncoder.OutContainer.WebM:
                videoArgs = "-c:v libvpx-vp9 -deadline good -cpu-used 4 -row-mt 1 -crf 30 -b:v 0 -pix_fmt yuv420p";
                audioArgs = "-c:a libopus -b:a 192k -ar 48000";
                break;
            case VideoEncoder.OutContainer.Mkv:
                videoArgs = "-c:v copy";
                audioArgs = "-c:a copy";
                break;
            case VideoEncoder.OutContainer.Mp4:
            case VideoEncoder.OutContainer.Mov:
                if (ext == ".mp4" || ext == ".mov")
                {
                    videoArgs = "-c:v copy";
                    audioArgs = "-c:a copy";
                }
                else if (target == VideoEncoder.OutContainer.Mov)
                {
                    videoArgs = "-c:v libx264 -preset veryfast -crf 23";
                    audioArgs = "-c:a aac -b:a 320k";
                }
                else
                {
                    videoArgs = "-c:v copy";
                    audioArgs = "-c:a aac -b:a 320k";
                }
                break;
            default:
                videoArgs = "-c:v copy";
                audioArgs = "-c:a copy";
                break;
        }

        string args = "-y -i " + Quote(path) + " " + videoArgs + " " + audioArgs
            + " " + Quote(partialPath);
        string retryArgs = audioArgs == "-c:a copy"
            ? "-y -i " + Quote(path) + " " + videoArgs + " -c:a aac -b:a 320k " + Quote(partialPath)
            : null;
        string fallbackArgs = target == VideoEncoder.OutContainer.Mov && (ext == ".mp4" || ext == ".mov")
            ? MovConversionFallback.BuildH264MovArguments(path, partialPath)
            : null;
        Exception error;
        bool ok = MovConversionFallback.TryConvert(
            attempt => TryRun(exePath, attempt), args, retryArgs, fallbackArgs,
            () => state.Messages.Add(Path.GetFileName(path) + ": AV1 video re-encoded to H.264 for MOV"),
            out error, out _);
        if (!ok)
        {
            try { File.Delete(partialPath); } catch { }
            throw error ?? new InvalidOperationException("conversion failed without an error");
        }
        try
        {
            // Move without overwrite is deliberate: a concurrent/newer output wins rather
            // than being silently replaced by this background conversion.
            File.Move(partialPath, targetPath);
        }
        catch
        {
            try { File.Delete(partialPath); } catch { }
            throw;
        }
        state.Converted++;
    }

    private static Exception TryRun(string exePath, string arguments)
    {
        try
        {
            VideoEncoder.RunAndWait(exePath, arguments);
            return null;
        }
        catch (Exception ex)
        {
            Debug.Log("[SpectreReplay] video convert attempt failed: " + ex.Message);
            return ex;
        }
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
