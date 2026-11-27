using System;

namespace SpectreReplay.Features.VideoRender;

// MOV 的 AV1 不支持提示来自 mux 阶段 stderr；无 ffprobe/codec 探测依赖，
// 但只对可识别的 AV1→MOV 容器拒绝回退，权限/磁盘/损坏输入等仍报失败。
internal static class MovConversionFallback
{
    internal static bool IsKnownAv1MovMuxFailure(string message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        string text = message.ToLowerInvariant();
        if (!text.Contains("av1")) return false;
        return text.Contains("only supported in mp4")
            || text.Contains("not currently supported in container")
            || text.Contains("codec not currently supported")
            || text.Contains("not supported by this container");
    }

    internal static string BuildH264MovArguments(string inputPath, string outputPath)
        => "-y -i " + Quote(inputPath)
            + " -c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p"
            + " -c:a aac -b:a 320k " + Quote(outputPath);

    // Execute normal copy, the existing audio-only retry, then a visible video transcode
    // only for the known AV1/MOV incompatibility. The delegate returns null on success.
    internal static bool TryConvert(Func<string, Exception> attempt,
        string initialArguments, string audioRetryArguments, string movH264FallbackArguments,
        Action onH264Fallback, out Exception error, out bool usedH264Fallback)
    {
        error = attempt(initialArguments);
        usedH264Fallback = false;
        if (error == null) return true;

        if (!string.IsNullOrEmpty(audioRetryArguments))
        {
            error = attempt(audioRetryArguments);
            if (error == null) return true;
        }

        if (!string.IsNullOrEmpty(movH264FallbackArguments)
            && IsKnownAv1MovMuxFailure(error.Message))
        {
            error = attempt(movH264FallbackArguments);
            if (error == null)
            {
                usedH264Fallback = true;
                onH264Fallback?.Invoke();
                return true;
            }
        }
        return false;
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
