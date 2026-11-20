// 参数测试不创建进程。只补齐真实 VideoEncoder 的定位/预热依赖；测试从不调用 WarmUp/Begin。
namespace UnityEngine
{
    internal static class Mathf
    {
        internal static int Clamp(int value, int min, int max)
            => System.Math.Max(min, System.Math.Min(max, value));
    }
}

namespace SpectreReplay
{
    internal static class Main
    {
        internal static ModStub mod;
        internal sealed class ModStub { internal string Path; }
    }

    internal static class Settings
    {
        internal static int RenderOutputFormat, RenderVideoCodec, RenderHardware, RenderCaptureMode;
        internal static int RenderWidth, RenderHeight, RenderFps, RenderEncoderMode, RenderQualityMode;
        internal static int RenderRateControl, RenderCrf, RenderCqp, RenderBitrateMbps, RenderPreset;
        internal static int RenderAudioOffsetMs;
        internal static bool Render10Bit, RenderHdr10, RenderCursorOn;
        internal static float RenderTailSeconds;
    }
}
