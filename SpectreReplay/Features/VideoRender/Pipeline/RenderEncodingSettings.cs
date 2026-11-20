namespace SpectreReplay.Features.VideoRender;

// 点击开始时冻结设置。后台预启动、尺寸重建和 CPU 重试必须沿用同一份参数。
internal sealed class RenderEncodingSettings
{
    internal readonly int Surface, Width, Height, Fps, Quality, QualityTier, Codec, Container;
    internal readonly int RateControl, Crf, Cqp, Bitrate, Preset, AudioOffsetMs;
    internal readonly bool Hdr10Requested, Hdr10, TenBit, Cursor;
    internal readonly float TailSeconds;
    internal readonly VideoEncoder.HardwareBackend Hardware;
    internal int? StartFloor;

    internal RenderEncodingSettings()
    {
        Surface = Settings.RenderCaptureMode;
        Width = Settings.RenderWidth;
        Height = Settings.RenderHeight;
        Fps = Settings.RenderFps;
        Quality = Settings.RenderEncoderMode;
        QualityTier = Settings.RenderQualityMode;
        Codec = Settings.RenderVideoCodec;
        Container = Settings.RenderOutputFormat;
        RateControl = Settings.RenderRateControl;
        Crf = Settings.RenderCrf;
        Cqp = Settings.RenderCqp;
        Bitrate = Settings.RenderBitrateMbps;
        Preset = Settings.RenderPreset;
        Hardware = (VideoEncoder.HardwareBackend)Settings.RenderHardware;
        Hdr10Requested = Settings.RenderHdr10;
        Hdr10 = RenderOutputPolicy.Hdr10Enabled(Hdr10Requested, Surface, Codec, Container);
        TenBit = Settings.Render10Bit || Hdr10;
        Cursor = Settings.RenderCursorOn;
        TailSeconds = Settings.RenderTailSeconds;
        AudioOffsetMs = Settings.RenderAudioOffsetMs;
    }
}
