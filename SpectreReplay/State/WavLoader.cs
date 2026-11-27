using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpectreReplay;

public static class WavLoader
{
    private struct WavHeader
    {
        public int FormatTag;

        public int Channels;

        public int SampleRate;

        public int BitsPerSample;

        public int DataOffset;

        public int DataSize;

        public int TotalSamples;

        // DataSize/TotalSamples 也必须校验：只有 fmt 块、没有 data 块的文件（录制中途
        // 断电只留下 44 字节头、下载被截断）能通过前四项，之后 ConvertWavToFloat 返回
        // 空数组、AudioClip.Create 拿到 0 长度，SetData 必然失败——错误现场离真正原因
        // （文件本身没有音频数据）很远，日志里看不出是文件坏了
        public bool IsValid => FormatTag > 0 && Channels > 0 && SampleRate > 0 && DataSize > 0 && TotalSamples > 0 && (BitsPerSample == 8 || BitsPerSample == 16 || BitsPerSample == 24 || BitsPerSample == 32);
    }

    public static AudioClip loaded_clip;

    internal static AudioSource musicSource;

    internal static GameObject audioGO;

    internal static AudioClip LoadFromMemory(byte[] wavData, string clipName = "WAV_Audio", bool streamAudio = false)
    {
        if (wavData == null || wavData.Length < 44)
        {
            Debug.LogError("[WavLoader] 数据无效或过小");
            return null;
        }
        // clip 必须在 try 外声明：AudioClip 是原生资源，Create 之后的任何异常路径
        // （SetData 反射调用失败、ConvertWavToFloat 之后的意外）都要能销毁它，
        // 否则每次加载失败泄漏一份原生内存。原代码只在"SetData 返回 false"时
        // Destroy，"SetData 抛异常"时直接被下面的 catch 吞掉并 return null。
        AudioClip val = null;
        try
        {
            WavHeader header = ParseHeader(wavData);
            if (!header.IsValid)
            {
                Debug.LogError("[WavLoader] WAV 头部解析失败");
                return null;
            }
            Debug.Log($"[WavLoader] 加载 WAV: {header.Channels}ch, {header.SampleRate}Hz, {header.BitsPerSample}bit, {header.TotalSamples} 样本");
            float[] array = ConvertWavToFloat(wavData, header);
            val = AudioClip.Create(clipName, header.TotalSamples, header.Channels, header.SampleRate, streamAudio);
            var setDataMethod = typeof(AudioClip).GetMethod("SetData", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float[]), typeof(int) }, null);
            if (setDataMethod == null)
            {
                // 走不到 Invoke（那会是 NullReferenceException，错误信息毫无指向性）
                Debug.LogError("[WavLoader] 找不到 AudioClip.SetData(float[], int)，加载中止");
                Object.Destroy(val);
                return null;
            }
            if (!(bool)setDataMethod.Invoke(val, new object[] { array, 0 }))
            {
                Debug.LogError("[WavLoader] SetData 失败");
                Object.Destroy(val);
                return null;
            }
            return val;
        }
        catch (Exception ex)
        {
            // 走到这里说明 Create 已经成功、但后续步骤抛了异常 → 必须销毁，
            // 否则每次加载失败泄漏一份原生内存
            if (val != null) Object.Destroy(val);
            Debug.LogError(("[WavLoader] 加载失败: " + ex.Message));
            return null;
        }
    }

    private static WavHeader ParseHeader(byte[] data)
    {
        WavHeader result = default(WavHeader);
        if (data == null || data.Length < 12 || !HasTag(data, 0, "RIFF") || !HasTag(data, 8, "WAVE"))
        {
            Debug.LogError("[WavLoader] 不是带 WAVE 标识的 RIFF 数据");
            return result;
        }

        uint riffSize = ReadUInt32(data, 4);
        if (riffSize < 4 || riffSize > (uint)(data.Length - 8))
        {
            Debug.LogError($"[WavLoader] RIFF 范围无效: {riffSize}");
            return result;
        }
        int riffEnd = checked(8 + (int)riffSize);
        int cursor = 12;
        bool haveFmt = false;
        bool haveData = false;
        while (cursor < riffEnd)
        {
            if (riffEnd - cursor < 8)
            {
                Debug.LogError("[WavLoader] RIFF 块头越界");
                return result;
            }
            string chunkId = Encoding.ASCII.GetString(data, cursor, 4);
            uint chunkSize = ReadUInt32(data, cursor + 4);
            if (chunkSize > int.MaxValue)
            {
                Debug.LogError($"[WavLoader] 块大小超过可读范围: {chunkSize}");
                return result;
            }
            long payloadEnd = cursor + 8L + chunkSize;
            long paddedEnd = payloadEnd + (chunkSize & 1);
            if (payloadEnd > riffEnd || paddedEnd > riffEnd || paddedEnd > data.Length)
            {
                Debug.LogError($"[WavLoader] 块范围越界: {chunkId} / {chunkSize}");
                return result;
            }

            int size = (int)chunkSize;
            int payload = cursor + 8;
            if (chunkId == "fmt ")
            {
                if (haveFmt || size < 16)
                {
                    Debug.LogError($"[WavLoader] fmt 块无效或重复: {size}");
                    return result;
                }
                haveFmt = true;
                int formatTag = ReadUInt16(data, payload);
                if (formatTag == 0xFFFE)
                {
                    // WAVEFORMATEXTENSIBLE 的 SubFormat GUID 从 fmt+24 开始；
                    // GUID 前两个字节保留 PCM=1 / IEEE float=3，剩余 GUID 字节
                    // 仍原样留在块中，不影响后续数据读取。
                    if (size < 40)
                    {
                        Debug.LogError($"[WavLoader] extensible fmt 块太小: {size}");
                        return result;
                    }
                    formatTag = ReadUInt16(data, payload + 24);
                }
                result.FormatTag = formatTag;
                if (formatTag != 1 && formatTag != 3)
                {
                    Debug.LogError($"[WavLoader] 不支持的音频格式: {formatTag}（仅支持 PCM=1 与 IEEE float=3）");
                    return result;
                }
                result.Channels = ReadUInt16(data, payload + 2);
                uint sampleRate = ReadUInt32(data, payload + 4);
                result.SampleRate = sampleRate > int.MaxValue ? 0 : (int)sampleRate;
                result.BitsPerSample = ReadUInt16(data, payload + 14);
                int blockAlign = ReadUInt16(data, payload + 12);
                if (result.Channels <= 0 || result.SampleRate <= 0)
                {
                    Debug.LogError("[WavLoader] fmt 声道数或采样率无效");
                    return result;
                }
                if (result.BitsPerSample != 8 && result.BitsPerSample != 16
                    && result.BitsPerSample != 24 && result.BitsPerSample != 32)
                {
                    Debug.LogError($"[WavLoader] 不支持的位深: {result.BitsPerSample}（支持 8/16/24/32）");
                    return result;
                }
                if (formatTag == 3 && result.BitsPerSample != 32)
                {
                    Debug.LogError("[WavLoader] IEEE float WAV 只支持 32 位");
                    return result;
                }
                long expectedAlign = (long)result.Channels * (result.BitsPerSample / 8);
                if (blockAlign <= 0 || expectedAlign != blockAlign)
                {
                    Debug.LogError($"[WavLoader] fmt blockAlign 无效: {blockAlign}");
                    return result;
                }
            }
            else if (chunkId == "data")
            {
                if (haveData)
                {
                    Debug.LogError("[WavLoader] 不支持多个 data 块，避免静默丢弃后续 PCM");
                    return result;
                }
                if (size <= 0)
                {
                    Debug.LogError($"[WavLoader] data 块大小无效: {size}");
                    return result;
                }
                haveData = true;
                result.DataSize = size;
                result.DataOffset = payload;
            }
            // 未知块必须连同奇数大小的一个 padding 字节一起跳过；否则下一个
            // chunk 的四字节 ID 会错位，合法 PCM 会被静默丢掉。
            cursor = checked((int)paddedEnd);
        }

        if (cursor != riffEnd || !haveFmt || !haveData)
        {
            Debug.LogError("[WavLoader] 缺少 fmt 或 data 块");
            return default(WavHeader);
        }
        long frameBytes = (long)result.Channels * (result.BitsPerSample / 8);
        if (frameBytes <= 0 || result.DataSize % frameBytes != 0)
        {
            Debug.LogError("[WavLoader] data 块不是完整 PCM 帧");
            return default(WavHeader);
        }
        long frames = result.DataSize / frameBytes;
        if (frames <= 0 || frames > int.MaxValue)
        {
            Debug.LogError($"[WavLoader] data 样本数无效: {frames}");
            return default(WavHeader);
        }
        result.TotalSamples = (int)frames;
        return result;
    }

    private static bool HasTag(byte[] data, int offset, string tag)
        => offset >= 0 && offset + 4 <= data.Length
           && data[offset] == tag[0] && data[offset + 1] == tag[1]
           && data[offset + 2] == tag[2] && data[offset + 3] == tag[3];

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8)
            | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    /// <summary>
    /// 统一替换动态键盘音 clip。AudioSource 先停并脱离旧 clip，再释放旧原生对象；
    /// 失败装载路径不会调用它，因此旧回放不会被半成品覆盖。
    /// </summary>
    internal static void ReplaceLoadedClip(AudioClip next)
    {
        AudioClip previous = loaded_clip;
        if (musicSource != null)
        {
            if (musicSource.isPlaying)
                musicSource.Stop();
            musicSource.clip = null;
        }
        loaded_clip = null;
        if (previous != null && previous != next)
            Object.Destroy(previous);
        loaded_clip = next;
    }

    internal static void ClearLoadedClip()
        => ReplaceLoadedClip(null);

    private static float[] ConvertWavToFloat(byte[] wavData, WavHeader header)
    {
        int num = header.BitsPerSample / 8;
        int num2 = header.DataSize / num;
        // 帧长为 channels*num，data 块大小不一定是整数帧（截断/编辑过的文件）。
        // 此时按字节算出的样本数会超过 AudioClip 长度，SetData 因长度不匹配直接返回
        // false → 整个音频加载失败。夹到整帧数，只丢尾部残帧即可正常加载。
        int frames = header.TotalSamples;
        if (num > 0 && frames > 0)
        {
            long frameSamplesLong = (long)frames * header.Channels;
            int frameSamples = frameSamplesLong > int.MaxValue ? int.MaxValue : (int)frameSamplesLong;
            if (num2 > frameSamples) num2 = frameSamples;
        }
        float[] array = new float[num2];
        for (int i = 0; i < num2; i++)
        {
            long longOffset = header.DataOffset + (long)i * num;
            if (longOffset + num > wavData.Length)
            {
                break;
            }
            int num3 = (int)longOffset;
            switch (header.BitsPerSample)
            {
                case 8:
                    array[i] = (float)(wavData[num3] - 128) / 128f;
                    break;
                case 16:
                    {
                        short num5 = (short)(wavData[num3] | (wavData[num3 + 1] << 8));
                        array[i] = (float)num5 / 32768f;
                        break;
                    }
                case 24:
                    {
                        int num6 = wavData[num3] | (wavData[num3 + 1] << 8) | (wavData[num3 + 2] << 16);
                        if ((num6 & 0x800000) != 0)
                        {
                            num6 |= -16777216;
                        }
                        array[i] = (float)num6 / 8388608f;
                        break;
                    }
                case 32:
                    {
                        if (header.FormatTag == 3)
                        {
                            array[i] = BitConverter.ToSingle(wavData, num3);
                            break;
                        }
                        int num4 = BitConverter.ToInt32(wavData, num3);
                        array[i] = (float)num4 / 2.1474836E+09f;
                        break;
                    }
                default:
                    array[i] = 0f;
                    break;
            }
        }
        return array;
    }

    internal static void Play_keybdsound(AudioClip snd, float volume)
    {
        if (musicSource == null || snd == null) return;
        musicSource.clip = snd;
        musicSource.volume = Mathf.Clamp01(volume);
        musicSource.Play();
        Debug.Log("开始播放！");
    }

    internal static void Stop_keybdsound()
    {
        if (musicSource != null)
            musicSource.Stop();
    }

    internal static void reset()
    {
        ClearLoadedClip();
        if (musicSource == null)
        {
            audioGO = new GameObject("Spectre_AudioSource");
            audioGO.hideFlags = (HideFlags)61;
            Object.DontDestroyOnLoad(audioGO);
            musicSource = audioGO.AddComponent<AudioSource>();
            musicSource.playOnAwake = false;
        }
    }
}
