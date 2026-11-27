using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpectreReplay;

/// <summary>
/// 纯 .NET 的键盘音 WAV 变换：播放端与导出端共用同一个「位置 = t + offset」契约。
///
/// 正偏移会裁掉 WAV 头后的前若干帧；负偏移会在 PCM 前补静音。未知 RIFF 块原样
/// 保留，奇数长度块的 padding 也会参与边界计算，但不把 padding 当成 PCM。
/// </summary>
internal static class WavAudioTransform
{
    private const ushort Pcm = 1;
    private const ushort IeeeFloat = 3;
    private const ushort Extensible = 0xFFFE;

    private sealed class Chunk
    {
        internal string Id;
        internal byte[] Payload;
    }

    private sealed class Format
    {
        internal ushort Tag;
        internal int Channels;
        internal int SampleRate;
        internal int BitsPerSample;
        internal int BlockAlign;
    }

    internal static bool TryApply(byte[] wav, int volumeStep, int offsetMs, bool muted,
        out byte[] output, out string error)
    {
        output = null;
        error = null;
        if (!TryParse(wav, out List<Chunk> chunks, out Format format,
            out List<byte[]> dataParts, out error))
            return false;

        long dataLength = 0;
        foreach (byte[] part in dataParts)
        {
            if (part == null || part.Length > int.MaxValue - dataLength)
            {
                error = "pcm_too_large";
                return false;
            }
            dataLength += part.Length;
        }
        if (dataLength == 0 || dataLength % format.BlockAlign != 0)
        {
            error = "pcm_frame_boundary";
            return false;
        }
        if (dataLength > int.MaxValue)
        {
            error = "pcm_too_large";
            return false;
        }

        byte[] source = new byte[(int)dataLength];
        int sourceOffset = 0;
        foreach (byte[] part in dataParts)
        {
            Buffer.BlockCopy(part, 0, source, sourceOffset, part.Length);
            sourceOffset += part.Length;
        }

        long sourceFrames = dataLength / format.BlockAlign;
        long offsetFrames = OffsetToFrames(offsetMs, format.SampleRate);
        long sourceStart = offsetFrames > 0 ? Math.Min(offsetFrames, sourceFrames) : 0;
        long prependFrames = offsetFrames < 0 ? -offsetFrames : 0;
        long outputFrames = sourceFrames - sourceStart + prependFrames;
        if (outputFrames <= 0 || format.BlockAlign <= 0
            || outputFrames > int.MaxValue / (long)format.BlockAlign)
        {
            error = "offset_too_large";
            return false;
        }
        long outputPcmLength = outputFrames * format.BlockAlign;
        if (outputPcmLength < 0 || outputPcmLength > int.MaxValue)
        {
            error = "offset_too_large";
            return false;
        }

        int step = Math.Max(0, Math.Min(10, volumeStep));
        bool silence = muted || step == 0;
        byte[] transformed = new byte[(int)outputPcmLength];
        double gain = step / 10.0;
        for (long outputFrame = 0; outputFrame < outputFrames; outputFrame++)
        {
            long sourceFrame = outputFrame - prependFrames + sourceStart;
            int outputByte = checked((int)(outputFrame * format.BlockAlign));
            if (sourceFrame < 0 || sourceFrame >= sourceFrames)
            {
                WriteSilentFrame(transformed, outputByte, format);
                continue;
            }
            int sourceByte = checked((int)(sourceFrame * format.BlockAlign));
            if (!silence && step == 10)
            {
                Buffer.BlockCopy(source, sourceByte, transformed, outputByte, format.BlockAlign);
            }
            else
            {
                ScaleFrame(source, sourceByte, transformed, outputByte, format, gain, silence);
            }
        }

        int firstData = -1;
        long totalLength = 12;
        for (int i = 0; i < chunks.Count; i++)
        {
            Chunk chunk = chunks[i];
            if (chunk.Id == "data")
            {
                if (firstData < 0)
                {
                    firstData = i;
                    totalLength += 8L + transformed.Length + (transformed.Length & 1);
                }
                continue;
            }
            totalLength += 8L + chunk.Payload.Length + (chunk.Payload.Length & 1);
        }
        if (firstData < 0 || totalLength > int.MaxValue || totalLength - 8 > uint.MaxValue)
        {
            error = "wav_too_large";
            return false;
        }

        output = new byte[(int)totalLength];
        WriteTag(output, 0, "RIFF");
        WriteUInt32(output, 4, (uint)(totalLength - 8));
        WriteTag(output, 8, "WAVE");
        int cursor = 12;
        for (int i = 0; i < chunks.Count; i++)
        {
            Chunk chunk = chunks[i];
            if (chunk.Id == "data")
            {
                if (i != firstData) continue;
                WriteTag(output, cursor, "data");
                WriteUInt32(output, cursor + 4, (uint)transformed.Length);
                Buffer.BlockCopy(transformed, 0, output, cursor + 8, transformed.Length);
                cursor += 8 + transformed.Length;
                if ((transformed.Length & 1) != 0) output[cursor++] = 0;
                continue;
            }
            WriteTag(output, cursor, chunk.Id);
            WriteUInt32(output, cursor + 4, (uint)chunk.Payload.Length);
            Buffer.BlockCopy(chunk.Payload, 0, output, cursor + 8, chunk.Payload.Length);
            // fact.dwSampleLength 是每声道帧数，偏移变换后必须与 data 同步。
            if (chunk.Id == "fact" && chunk.Payload.Length >= 4)
                WriteUInt32(output, cursor + 8, (uint)outputFrames);
            cursor += 8 + chunk.Payload.Length;
            if ((chunk.Payload.Length & 1) != 0) output[cursor++] = 0;
        }
        return true;
    }

    private static long OffsetToFrames(int offsetMs, int sampleRate)
    {
        // 播放端把 offsetMs/1000f 加到秒级定位；按帧落盘时向零截断，避免正偏移
        // 在 44.1kHz 下把 1ms 错误地变成 45 帧而提前吃掉一个采样。
        return (long)((double)offsetMs * sampleRate / 1000.0);
    }

    private static bool TryParse(byte[] wav, out List<Chunk> chunks, out Format format,
        out List<byte[]> dataParts, out string error)
    {
        chunks = new List<Chunk>();
        format = null;
        dataParts = new List<byte[]>();
        error = null;
        if (wav == null || wav.Length < 12)
        {
            error = "wav_too_small";
            return false;
        }
        if (!HasTag(wav, 0, "RIFF") || !HasTag(wav, 8, "WAVE"))
        {
            error = "not_riff_wave";
            return false;
        }
        uint declaredSize = ReadUInt32(wav, 4);
        if (declaredSize < 4 || declaredSize > (uint)(wav.Length - 8))
        {
            error = "riff_bounds";
            return false;
        }
        int end = checked(8 + (int)declaredSize);
        int cursor = 12;
        while (cursor < end)
        {
            if (end - cursor < 8)
            {
                error = "chunk_header_bounds";
                return false;
            }
            string id = Encoding.ASCII.GetString(wav, cursor, 4);
            uint size = ReadUInt32(wav, cursor + 4);
            if (size > int.MaxValue)
            {
                error = "chunk_too_large";
                return false;
            }
            long payloadEnd = cursor + 8L + size;
            long paddedEnd = payloadEnd + (size & 1);
            if (payloadEnd > end || paddedEnd > end || paddedEnd > wav.Length)
            {
                error = "chunk_bounds";
                return false;
            }
            byte[] payload = new byte[(int)size];
            Buffer.BlockCopy(wav, cursor + 8, payload, 0, payload.Length);
            Chunk chunk = new Chunk { Id = id, Payload = payload };
            chunks.Add(chunk);
            if (id == "fmt ")
            {
                if (format != null || !TryParseFormat(payload, out format, out error))
                    return false;
            }
            else if (id == "data")
            {
                dataParts.Add(payload);
            }
            cursor = checked((int)paddedEnd);
        }
        if (cursor != end || format == null || dataParts.Count == 0)
        {
            error = format == null ? "fmt_missing" : "data_missing";
            return false;
        }
        return true;
    }

    private static bool TryParseFormat(byte[] payload, out Format format, out string error)
    {
        format = null;
        error = null;
        if (payload == null || payload.Length < 16)
        {
            error = "fmt_too_small";
            return false;
        }
        ushort tag = ReadUInt16(payload, 0);
        if (tag == Extensible)
        {
            if (payload.Length < 40)
            {
                error = "fmt_ext_too_small";
                return false;
            }
            tag = ReadUInt16(payload, 24);
        }
        if (tag != Pcm && tag != IeeeFloat)
        {
            error = "fmt_unsupported";
            return false;
        }
        int channels = ReadUInt16(payload, 2);
        uint sampleRateRaw = ReadUInt32(payload, 4);
        int sampleRate = sampleRateRaw > int.MaxValue ? 0 : (int)sampleRateRaw;
        int blockAlign = ReadUInt16(payload, 12);
        int bits = ReadUInt16(payload, 14);
        if (channels <= 0 || sampleRate <= 0 || blockAlign <= 0
            || (bits != 8 && bits != 16 && bits != 24 && bits != 32)
            || (tag == IeeeFloat && bits != 32))
        {
            error = "fmt_invalid";
            return false;
        }
        long expectedAlign = (long)channels * (bits / 8);
        if (expectedAlign != blockAlign)
        {
            error = "fmt_block_align";
            return false;
        }
        format = new Format
        {
            Tag = tag,
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = bits,
            BlockAlign = blockAlign
        };
        return true;
    }

    private static void ScaleFrame(byte[] source, int sourceByte, byte[] output, int outputByte,
        Format format, double gain, bool silence)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        for (int channel = 0; channel < format.Channels; channel++)
        {
            int from = sourceByte + channel * bytesPerSample;
            int to = outputByte + channel * bytesPerSample;
            if (silence)
            {
                WriteSilence(output, to, bytesPerSample);
                continue;
            }
            switch (format.Tag)
            {
                case Pcm:
                    ScalePcm(source, from, output, to, format.BitsPerSample, gain);
                    break;
                case IeeeFloat:
                    float value = BitConverter.ToSingle(source, from);
                    if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
                    WriteInt32(output, to, BitConverter.SingleToInt32Bits((float)(value * gain)));
                    break;
            }
        }
    }

    private static void ScalePcm(byte[] source, int from, byte[] output, int to,
        int bits, double gain)
    {
        switch (bits)
        {
            case 8:
                int u8 = source[from] - 128;
                output[to] = (byte)Math.Max(0, Math.Min(255,
                    (int)Math.Round(u8 * gain, MidpointRounding.AwayFromZero) + 128));
                break;
            case 16:
                short i16 = (short)(source[from] | (source[from + 1] << 8));
                long s16 = (long)Math.Round(i16 * gain, MidpointRounding.AwayFromZero);
                WriteInt16(output, to, (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s16)));
                break;
            case 24:
                int i24 = source[from] | (source[from + 1] << 8) | (source[from + 2] << 16);
                if ((i24 & 0x800000) != 0) i24 |= unchecked((int)0xFF000000);
                long s24 = (long)Math.Round(i24 * gain, MidpointRounding.AwayFromZero);
                s24 = Math.Max(-8388608L, Math.Min(8388607L, s24));
                output[to] = (byte)s24;
                output[to + 1] = (byte)(s24 >> 8);
                output[to + 2] = (byte)(s24 >> 16);
                break;
            case 32:
                int i32 = BitConverter.ToInt32(source, from);
                double scaled = Math.Round(i32 * gain, MidpointRounding.AwayFromZero);
                if (scaled < int.MinValue) scaled = int.MinValue;
                if (scaled > int.MaxValue) scaled = int.MaxValue;
                WriteInt32(output, to, (int)scaled);
                break;
        }
    }

    private static void WriteSilentFrame(byte[] bytes, int offset, Format format)
    {
        if (format.Tag == Pcm && format.BitsPerSample == 8)
        {
            for (int channel = 0; channel < format.Channels; channel++)
                bytes[offset + channel] = 128;
            return;
        }
        Array.Clear(bytes, offset, format.BlockAlign);
    }

    private static void WriteSilence(byte[] bytes, int offset, int length)
    {
        if (length == 1)
        {
            bytes[offset] = 128;
            return;
        }
        Array.Clear(bytes, offset, length);
    }

    private static bool HasTag(byte[] bytes, int offset, string tag)
        => offset >= 0 && offset + 4 <= bytes.Length
           && bytes[offset] == tag[0] && bytes[offset + 1] == tag[1]
           && bytes[offset + 2] == tag[2] && bytes[offset + 3] == tag[3];

    private static void WriteTag(byte[] bytes, int offset, string tag)
    {
        bytes[offset] = (byte)tag[0];
        bytes[offset + 1] = (byte)tag[1];
        bytes[offset + 2] = (byte)tag[2];
        bytes[offset + 3] = (byte)tag[3];
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
        => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ReadUInt32(byte[] bytes, int offset)
        => (uint)(bytes[offset] | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] bytes, int offset, short value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }
}
