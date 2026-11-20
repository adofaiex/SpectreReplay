using System;
using System.IO;

namespace UiPreview;

/// <summary>
/// 视频编码器矩阵（H.264/H.265 + 码率模式 + preset）的离线形状检查。
///
/// 为什么需要：这些路径的失败在游戏里表现为「参数不被 ffmpeg 接受」或
/// 「选了 H.265 实际仍出 H.264」，控制台可能只有一行 warning，玩家只会觉得
/// 成片不对。离线把枚举、参数分支、设置持久化和界面入口钉住。
/// </summary>
internal static class EncoderMatrixCheck
{
    internal static (int Total, int Failed) Run()
    {
        int total = 0, failed = 0;
        string enc = Read("Features/VideoRender/Pipeline/VideoEncoder.cs");
        string catalog = Read("Features/VideoRender/Pipeline/EncoderCatalog.cs");
        string cap = Read("../tools/ffmpeg-capability.ps1");
        string wf = Read("../.github/workflows/build.yml");
        string job = Read("Features/VideoRender/Pipeline/VideoRenderJob.cs");
        string settings = Read("State/Settings.cs");
        string cfg = Read("State/ConfigManager.cs");
        string pages = Read("Pages/Pages.cs");

        Console.WriteLine("  ── 视频编码器矩阵 ──");

        Check(enc.Contains("internal enum VideoCodec { H264 = 0, H265 = 1, Av1 = 2 }", StringComparison.Ordinal),
            "① VideoEncoder 有 H.264/H.265/AV1 枚举");
        Check(enc.Contains("if (videoCodec == VideoCodec.Av1)", StringComparison.Ordinal)
            && enc.Contains("return new[] { Codec.NvencAv1, Codec.QsvAv1, Codec.AmfAv1 };", StringComparison.Ordinal)
            && enc.Contains("case Codec.NvencAv1:", StringComparison.Ordinal)
            && enc.Contains("case Codec.QsvAv1:", StringComparison.Ordinal)
            && enc.Contains("case Codec.AmfAv1:", StringComparison.Ordinal),
            "① AV1 候选链与硬件分支在");
        Check(enc.Contains("EncoderCatalog.Supports(exe, \"libsvtav1\")", StringComparison.Ordinal)
            && enc.Contains("EncoderCatalog.Supports(exe, \"libaom-av1\")", StringComparison.Ordinal)
            && enc.Contains("return Codec.SvtAv1;", StringComparison.Ordinal)
            && enc.Contains("return Codec.AomAv1;", StringComparison.Ordinal),
            "① AV1 软件优先 SVT，退回 libaom");
        Check(enc.Contains("-c:v av1_nvenc", StringComparison.Ordinal)
            && enc.Contains("-c:v libsvtav1", StringComparison.Ordinal)
            && enc.Contains("-c:v libaom-av1", StringComparison.Ordinal),
            "① AV1 编码命令在");
        Check(enc.Contains("return \"-b:v \" + target + \"M -bufsize \" + bucket + \"M\";", StringComparison.Ordinal)
            && enc.Contains("return \"-b:v \" + target + \"M\";", StringComparison.Ordinal),
            "② SVT ABR/CBR 不传 -maxrate（libsvtav1 会拒绝）");
        Check(enc.Contains("internal enum Quality { Balanced = 0, Fastest = 1, Sharpest = 2 }", StringComparison.Ordinal)
            && !enc.Contains("Quality.CpuOnly", StringComparison.Ordinal),
            "① Quality 只保留速度/画质，CpuOnly 已删");
        Check(enc.Contains("internal enum HardwareBackend { Auto = 0, Nvenc = 1, Qsv = 2, Amf = 3, Cpu = 4 }", StringComparison.Ordinal),
            "① 硬件后端枚举齐全");
        Check(enc.Contains("-c:v libx265 -preset", StringComparison.Ordinal),
            "① H.265 走 libx265");
        Check(enc.Contains("-c:v hevc_nvenc -preset", StringComparison.Ordinal),
            "① H.265 硬件走 hevc_nvenc");
        Check(enc.Contains("if (container == OutContainer.WebM) return Codec.Vp9;", StringComparison.Ordinal),
            "① WebM 强制 VP9（H.264/H.265 不进 WebM）");
        Check(enc.Contains("if (container == OutContainer.Mov && videoCodec == VideoCodec.Av1)", StringComparison.Ordinal)
            && enc.Contains("return Codec.X265;", StringComparison.Ordinal)
            && enc.Contains("MOV 容器不支持 AV1", StringComparison.Ordinal),
            "① MOV + AV1 回退 H.265 并告警（mov muxer 拒绝 AV1）");
        Check(enc.Contains("private string SoftwarePreset()", StringComparison.Ordinal),
            "① 软件编码有独立 preset 映射");
        Check(enc.Contains("ProbeEncoderSync(exe, encoderName)", StringComparison.Ordinal)
            && catalog.Contains("\"-hide_banner -encoders\"", StringComparison.Ordinal),
            "① 通用编码器目录 + preflight 在");
        Check(catalog.Contains("internal static bool Supports(string exe, string encoderName)", StringComparison.Ordinal)
            && catalog.Contains("internal static bool IsLoaded(string exe)", StringComparison.Ordinal),
            "① EncoderCatalog 有 Supports/IsLoaded 接口");
        Check(catalog.Contains("return set.Contains(encoderName);", StringComparison.Ordinal),
            "① Supports 按编码器清单精确匹配");

        Check(enc.Contains("internal enum RateControlMode { Abr = 0, Crf = 1, Cqp = 2, Cbr = 3 }", StringComparison.Ordinal),
            "② 码率模式枚举齐全");
        Check(enc.Contains("return \"-crf \" + crf;", StringComparison.Ordinal),
            "② CRF 参数在");
        Check(enc.Contains("\"-crf \" + crf + \" -b:v 0\"", StringComparison.Ordinal),
            "② VP9 CRF 配 -b:v 0（恒定质量）");
        Check(enc.Contains("else if (rateControl == RateControlMode.Cqp)", StringComparison.Ordinal)
            && enc.Contains("rc = BuildAbrRateControl(false);", StringComparison.Ordinal),
            "② VP9 CQP 退回 ABR（libvpx 没有 -qp）");
        Check(enc.Contains("return \"-qp \" + cqp;", StringComparison.Ordinal),
            "② CQP 参数在");
        Check(enc.Contains("-rc cbr", StringComparison.Ordinal),
            "② CBR 参数在");
        Check(enc.Contains("if (nvenc)", StringComparison.Ordinal)
            && enc.Contains("-rc constqp -qp", StringComparison.Ordinal)
            && enc.Contains("-rc vbr -cq", StringComparison.Ordinal),
            "② NVENC 的 CRF/CQP/ABR 映射在");

        Check(settings.Contains("public static int RenderVideoCodec = 0;", StringComparison.Ordinal)
            && settings.Contains("public static int RenderHardware = 0;", StringComparison.Ordinal)
            && settings.Contains("public static int RenderRateControl = 0;", StringComparison.Ordinal)
            && settings.Contains("public static int RenderBitrateMbps = 0;", StringComparison.Ordinal)
            && settings.Contains("public static int RenderPreset = 0;", StringComparison.Ordinal),
            "③ Settings 新增编码字段");
        Check(settings.Contains("RenderVideoCodec = Mathf.Clamp(RenderVideoCodec, 0, 2);", StringComparison.Ordinal)
            && settings.Contains("RenderEncoderMode = Mathf.Clamp(RenderEncoderMode, 0, 2);", StringComparison.Ordinal)
            && settings.Contains("RenderHardware = Mathf.Clamp(RenderHardware, 0, 4);", StringComparison.Ordinal)
            && settings.Contains("RenderRateControl = Mathf.Clamp(RenderRateControl, 0, 3);", StringComparison.Ordinal)
            && settings.Contains("RenderCrf = Mathf.Clamp(RenderCrf, 0, 51);", StringComparison.Ordinal)
            && settings.Contains("RenderPreset = Mathf.Clamp(RenderPreset, 0, 10);", StringComparison.Ordinal),
            "③ Settings 归一化编码字段");

        Check(cfg.Contains("render_video_codec = Settings.RenderVideoCodec,", StringComparison.Ordinal)
            && cfg.Contains("render_rate_control = Settings.RenderRateControl,", StringComparison.Ordinal)
            && cfg.Contains("render_hardware = Settings.RenderHardware,", StringComparison.Ordinal)
            && cfg.Contains("render_preset = Settings.RenderPreset,", StringComparison.Ordinal),
            "④ ConfigManager 落盘编码字段");
        Check(cfg.Contains("ints.Value<int?>(\"render_video_codec\")", StringComparison.Ordinal)
            && cfg.Contains("ints.Value<int?>(\"render_rate_control\")", StringComparison.Ordinal)
            && cfg.Contains("ints.Value<int?>(\"render_hardware\")", StringComparison.Ordinal)
            && cfg.Contains("ints.Value<int?>(\"render_preset\")", StringComparison.Ordinal),
            "④ ConfigManager 读取编码字段（旧配置缺键走默认）");
        Check(cfg.Contains("Settings.RenderVideoCodec = Mathf.Clamp(ints.Value<int?>(\"render_video_codec\") ?? Settings.RenderVideoCodec, 0, 2);", StringComparison.Ordinal),
            "④ render_video_codec clamp 0..2（AV1 不被压回 H.265）");
        Check(cfg.Contains("if (rawHardware == null && rawEncoder == 3)", StringComparison.Ordinal)
            && cfg.Contains("Settings.RenderHardware = 4;", StringComparison.Ordinal)
            && cfg.Contains("Settings.RenderEncoderMode = 2;", StringComparison.Ordinal),
            "④ 旧 render_encoder=3 在 clamp 前迁移为 CPU 后端");
        Check(cfg.Contains("hardwareValue < 0 || hardwareValue > 4", StringComparison.Ordinal)
            && cfg.Contains("hardwareValue = 0;", StringComparison.Ordinal)
            && cfg.Contains("Settings.RenderHardware = hardwareValue;", StringComparison.Ordinal),
            "④ 非法 render_hardware 回 Auto，不 clamp 到 CPU");

        Check(job.Contains("(VideoEncoder.VideoCodec)options.Codec", StringComparison.Ordinal)
            && job.Contains("(VideoEncoder.RateControlMode)options.RateControl", StringComparison.Ordinal)
            && job.Contains("job.options.Hardware", StringComparison.Ordinal)
            && job.Contains("options.Bitrate, options.Preset", StringComparison.Ordinal),
            "⑤ 渲染作业把编码参数传给 VideoEncoder");

        Check(pages.Contains("OnGui.RowDropdown(\"render.video_codec\"", StringComparison.Ordinal)
            && pages.Contains("\"H264\", \"H265\", \"AV1\"", StringComparison.Ordinal)
            && pages.Contains("OnGui.RowDropdown(\"render.hardware\"", StringComparison.Ordinal)
            && pages.Contains("OnGui.RowDropdown(\"render.rate_control\"", StringComparison.Ordinal)
            && pages.Contains("OnGui.RowDropdown(\"render.preset\"", StringComparison.Ordinal)
            && pages.Contains("OnGui.RowSlider(\"render.crf\"", StringComparison.Ordinal)
            && pages.Contains("OnGui.RowSlider(\"render.cqp\"", StringComparison.Ordinal)
            && pages.Contains("OnGui.RowSlider(\"render.bitrate\"", StringComparison.Ordinal),
            "⑥ 界面暴露视频编码/码率模式/preset 入口");
        Check(pages.Contains("(VideoEncoder.HardwareBackend)Mathf.Clamp(RenderHardware, 0, 4)", StringComparison.Ordinal)
            && pages.Contains("(VideoEncoder.VideoCodec)Mathf.Clamp(RenderVideoCodec, 0, 2)", StringComparison.Ordinal)
            && pages.Contains("VideoEncoder.DescribeEncoder(", StringComparison.Ordinal),
            "⑥ 编码路径下拉已瘦身，后端由 hardware 传入");
        Check(pages.Contains("render.codec.mov_av1_fallback", StringComparison.Ordinal)
            && pages.Contains("render.codec.svt_cbr", StringComparison.Ordinal)
            && pages.Contains("render.codec.aom_slow", StringComparison.Ordinal)
            && pages.Contains("render.codec.hw_fallback", StringComparison.Ordinal),
            "⑥ 回退原因提示在（MOV/AV1、SVT CBR、libaom、硬件不可用）");
        Check(enc.Contains("resolved = ResolveCodec(container, videoCodec, hardware, tenBit, hdr10);", StringComparison.Ordinal)
            && !enc.Contains("ResolveCodec(quality, container, videoCodec", StringComparison.Ordinal),
            "⑦ DescribeEncoder 与 PickCodec 共用同一判定，quality 已退出后端判定");
        Check(enc.Contains("private static Codec[] Candidates(VideoCodec videoCodec, HardwareBackend hardware, string exe)", StringComparison.Ordinal)
            && enc.Contains("EncoderStateFor(exe, EncoderName(candidate))", StringComparison.Ordinal)
            && enc.Contains("private static int EncoderStateFor(string exe, string encoderName)\n    {\n        if (!EncoderCatalog.IsLoaded(exe))", StringComparison.Ordinal)
            && enc.Contains("EncoderCatalog.EnsureLoaded(exe)", StringComparison.Ordinal),
            "⑦ Auto 链 NVENC→QSV→AMF→软件，目录未就绪先加载");
        Check(enc.Contains("case Codec.QsvH265:", StringComparison.Ordinal)
            && enc.Contains("case Codec.QsvH264:", StringComparison.Ordinal)
            && enc.Contains("case Codec.AmfH265:", StringComparison.Ordinal)
            && enc.Contains("case Codec.AmfH264:", StringComparison.Ordinal)
            && enc.Contains("return \"-global_quality \" + crf;", StringComparison.Ordinal)
            && enc.Contains("return \"-flags +qscale -q:v \" + cqp;", StringComparison.Ordinal),
            "⑦ QSV/AMF 编码分支与 QSV 质量参数在");

        Check(cap.Contains("'libx264'", StringComparison.Ordinal)
            && cap.Contains("'libx265'", StringComparison.Ordinal)
            && cap.Contains("'libvpx-vp9'", StringComparison.Ordinal)
            && cap.Contains("'aac'", StringComparison.Ordinal)
            && cap.Contains("'libopus'", StringComparison.Ordinal)
            && cap.Contains("-notmatch 'yuv420p10le'", StringComparison.Ordinal),
            "⑧ CI 能力断言覆盖核心编码器与 10-bit");
        Check(cap.Contains("'libsvtav1'", StringComparison.Ordinal)
            && cap.Contains("'libaom-av1'", StringComparison.Ordinal)
            && cap.Contains("'av1_nvenc'", StringComparison.Ordinal)
            && cap.Contains("'av1_qsv'", StringComparison.Ordinal)
            && cap.Contains("'av1_amf'", StringComparison.Ordinal),
            "⑧ CI 能力断言覆盖 AV1 变体");
        Check(cap.Contains("'h264_qsv'", StringComparison.Ordinal)
            && cap.Contains("'hevc_qsv'", StringComparison.Ordinal)
            && cap.Contains("'h264_amf'", StringComparison.Ordinal)
            && cap.Contains("'hevc_amf'", StringComparison.Ordinal),
            "⑧ CI 能力断言覆盖 QSV/AMF H.264/H.265 名单");
        Check(cap.Contains("ffmpeg-encoders.txt", StringComparison.Ordinal)
            && wf.Contains("ffmpeg-capability.ps1", StringComparison.Ordinal)
            && wf.Contains("upload-artifact", StringComparison.Ordinal),
            "⑧ CI 失败时保存 -encoders 输出");

        Console.WriteLine($"  视频编码器矩阵：{(total == 0 ? "已检查" : "")} {total} 条，失败 {failed}");
        return (total, failed);

        void Check(bool ok, string label)
        {
            total++;
            Console.WriteLine((ok ? "    ✓ " : "    ✗ ") + label);
            if (!ok) failed++;
        }
    }

    private static string Read(string rel)
        => SrcUtil.Source(Driver.ProjectDir, rel);
}
