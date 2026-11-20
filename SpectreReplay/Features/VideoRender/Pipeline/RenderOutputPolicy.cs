namespace SpectreReplay.Features.VideoRender;

// 帧源、编码器与设置页共用的生效规则。参数是持久化枚举的数值，不依赖 Unity。
internal static class RenderOutputPolicy
{
    internal static bool Hdr10Enabled(bool requested, int surface, int codec, int container)
        => requested && surface == 0 && codec == 1 && container != 2;

    internal static bool UseTimelineRange(bool hasSource, double duration, double start, double end)
        => hasSource && duration > 0.0 && (start > 0.01 || end < duration - 0.01);

    internal static bool DrainComplete(int inFlight, int delivered, int captured)
        => inFlight == 0 && delivered == captured;
}
