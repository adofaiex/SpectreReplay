using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpectreReplay;

namespace UiPreview;

/// <summary>
/// 音频修复的离线钉子：直接运行 Replay/Codec 下的纯 WAV 与设备策略 helper，
/// 并检查 Unity 依赖路径的生命周期/加载顺序接线。父会话需把本文件、
/// Features/Replay/Codec/WavAudioTransform.cs 与 MicrophoneDevicePolicy.cs 链入 UiPreview。
/// </summary>
internal static class AudioRepairCheck
{
    private static int _fail;

    internal static int Run()
    {
        _fail = 0;
        Console.WriteLine("  ── 音频修复（WAV / volume-offset / 麦克风 / 生命周期） ──");
        OddChunkAndPadding();
        WavParserBehavior();
        VolumeOffsetMuteStereo();
        ExportBoundaryBehavior();
        MemoryBackedExportBehavior();
        MicrophoneFallback();
        LifecycleAndLoadShape();
        if (_fail == 0) Console.WriteLine("    ✓ 音频修复全部通过");
        return _fail;
    }

    private static void WavParserBehavior()
    {
        try
        {
            var root = CSharpSyntaxTree.ParseText(SrcUtil.Read(Driver.ProjectDir, "State/WavLoader.cs")).GetRoot();
            var type = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "WavLoader");
            string[] names = { "ParseHeader", "HasTag", "ReadUInt16", "ReadUInt32", "ConvertWavToFloat", "ReplaceLoadedClip" };
            string members = string.Join("\n", type.Members.Where(m => m is StructDeclarationSyntax
                || m is MethodDeclarationSyntax method && names.Contains(method.Identifier.ValueText)));
            string code = "using System; using System.Text; using Object = FakeObject; public static class Fixture { "
                + "static FakeClip loaded_clip; static FakeSource musicSource; " + members + """
                public static bool Run(byte[] wav)
                {
                    var header = ParseHeader(wav);
                    if (!header.IsValid || header.Channels != 1 || header.SampleRate != 1000 || header.TotalSamples != 2) return false;
                    float[] samples = ConvertWavToFloat(wav, header);
                    if (samples.Length != 2 || samples[0] <= 0 || samples[1] >= 0) return false;
                    byte[] broken = (byte[])wav.Clone(); broken[40] = 255;
                    if (ParseHeader(broken).IsValid) return false;
                    var previous = new FakeClip(); var next = new FakeClip();
                    loaded_clip = previous; musicSource = new FakeSource { clip = previous, isPlaying = true };
                    ReplaceLoadedClip(next);
                    return previous.Destroyed && musicSource.Stopped && musicSource.clip == null && loaded_clip == next;
                }
                }
                class FakeClip { public bool Destroyed; }
                class FakeSource { public FakeClip clip; public bool isPlaying, Stopped; public void Stop() { isPlaying = false; Stopped = true; } }
                static class FakeObject { public static void Destroy(FakeClip clip) { clip.Destroyed = true; } }
                static class Debug { public static void LogError(string value) { } }
                """;
            code = code.Replace("AudioClip", "FakeClip", StringComparison.Ordinal);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("WavLoaderRepair_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(code) }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var result = compilation.Emit(image);
            if (!result.Success) throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)));
            var fixture = Assembly.Load(image.ToArray()).GetType("Fixture");
            Check((bool)fixture.GetMethod("Run").Invoke(null, new object[] { MakeWav(1000, 1, new short[] {1000, -1000}, true) }),
                "AR21 实际 WavLoader 解析 odd padding/PCM，拒坏块并释放旧 clip", "");
        }
        catch (Exception ex) { Check(false, "AR21 WavLoader 真源码行为", (ex.InnerException ?? ex).Message); }
    }

    private static void ExportBoundaryBehavior()
    {
        byte[] input = MakeWav(1000, 1, new short[] { 1000, -1000 }, false);
        bool emptyOk = WavAudioTransform.TryApply(input, 10, 100, false, out byte[] empty, out _);
        Check(!emptyOk && empty == null, "AR23 正偏移裁空音频时明确失败，不输出不可重载的零样本 WAV", "");
        byte[] pcm = MakeWav(1000, 1, new short[] { 0, 0, 0, 0 }, false);
        Put16(pcm, 20, 3); Put32(pcm, 28, 4000); Put16(pcm, 32, 4); Put16(pcm, 34, 32);
        byte[] withFact = new byte[pcm.Length + 12];
        Buffer.BlockCopy(pcm, 0, withFact, 0, 36);
        Encoding.ASCII.GetBytes("fact").CopyTo(withFact, 36); Put32(withFact, 40, 4); Put32(withFact, 44, 2);
        Buffer.BlockCopy(pcm, 36, withFact, 48, pcm.Length - 36); Put32(withFact, 4, withFact.Length - 8);
        bool factOk = WavAudioTransform.TryApply(withFact, 10, 1, false, out byte[] output, out string error);
        Check(factOk && BitConverter.ToInt32(output, 44) == 1 && FirstData(output)?.Length == 4,
            "AR24 float WAV 偏移后 fact 与 PCM 帧数同步", error ?? "");
    }

    private static void MemoryBackedExportBehavior()
    {
        try
        {
            string root = Driver.ProjectDir;
            var managerRoot = CSharpSyntaxTree.ParseText(Read(root, @"Features\Replay\Runtime\ReplayManager.cs")).GetRoot();
            var manager = managerRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(t => t.Identifier.ValueText == "ReplayManager");
            string MethodSource(string name) => manager.Members.OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == name).ToString();
            var loadMethod = manager.Members.OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == "TryLoadKeybdSoundFromMemory");
            var cacheBlock = loadMethod.DescendantNodes().OfType<IfStatementSyntax>()
                .Single(i => i.Condition.ToString() == "nextClip != null");
            string legacyLoad = loadMethod
                .RemoveNode(cacheBlock, SyntaxRemoveOptions.KeepExteriorTrivia).ToString()
                .Replace("private static void TryLoadKeybdSoundFromMemory",
                    "public static void TryLoadKeybdSoundFromMemory", StringComparison.Ordinal);

            var dataRoot = CSharpSyntaxTree.ParseText(Read(root, @"Features\Replay\Data\ReplayData.cs")).GetRoot();
            var data = dataRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Single(t => t.Identifier.ValueText == "ReplayData");
            string cacheProperty = data.Members.OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == "KeybdSoundMemory").ToString();
            var reset = data.Members.OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == "Reset");
            string resetLine = reset.Body.Statements
                .Single(s => s is ExpressionStatementSyntax statement
                    && statement.Expression is AssignmentExpressionSyntax assignment
                    && assignment.Left.ToString() == "KeybdSoundMemory").ToString();

            string fixture = """
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using SpectreReplay;

namespace UnityEngine { public sealed class AudioClip { } }

namespace SpectreReplay
{
    public static class Settings
    {
        public static string SavePath;
        public static int KeybdSoundVolume = 10;
        public static int MicrophoneOffset;
    }
    public static class SpectreState
    {
        public static bool DebugMode;
        public static bool debug_skip_Verification;
        public static bool HasKeybdSound;
    }
    public static class AudioRecorder
    {
        public static bool VerificationResult;
        public static bool VerifyAudioData(byte[] wav, string hash) => VerificationResult;
    }
    public static class WavLoader
    {
        public static AudioClip loaded_clip;
        public static bool FailClipCreation;
        public static AudioClip LoadFromMemory(byte[] wav, string name)
            => FailClipCreation ? null : new AudioClip();
        public static void ReplaceLoadedClip(AudioClip clip) => loaded_clip = clip;
    }
    public static class AtomicFile
    {
        public static void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
    }
    public static class Debug
    {
        public static void LogError(string message) { }
        public static void LogWarning(string message) { }
    }
}
namespace SpectreReplay.Features.Replay
{
    public struct KeybdAudioBlob { public bool HasData; }
    public struct KeybdPrefs
    {
        public bool HasVolume;
        public int Volume;
        public bool HasOffset;
        public int OffsetMs;
        public bool IsMuted;
    }
    public sealed class ReplayData
    {
        public string FileName { get; set; }
        public string KeybdSoundFileName { get; set; }
        public string KeybdSoundHash { get; set; }
        public string KeybdSoundSourcePath { get; set; }
        public KeybdAudioBlob KeybdAudio { get; set; }
        public KeybdPrefs KeybdPrefs { get; set; }
        __CACHE_PROPERTY__
        public void Reset() { __RESET_LINE__ }
    }
    public static class KeybdAudioCodec
    {
        public static byte[] DecodeToWav(KeybdAudioBlob blob) => null;
    }
    public static class ReplayManagerFixture
    {
        private static void TryLoadKeybdSoundFromBlob(ReplayData data, string path) { }
        __REMOVE_INVALID_PATH_CHARS__
        __TRY_LOAD__
        __IS_PURE_FILE_NAME__
        __EXPORT__

        private static byte[] DataBytes(byte[] wav)
        {
            if (wav == null || wav.Length < 12) return null;
            for (int p = 12; p + 8 <= wav.Length;)
            {
                int size = BitConverter.ToInt32(wav, p + 4);
                if (size < 0 || p + 8L + size > wav.Length) return null;
                if (Encoding.ASCII.GetString(wav, p, 4) == "data")
                {
                    byte[] data = new byte[size];
                    Buffer.BlockCopy(wav, p + 8, data, 0, size);
                    return data;
                }
                p += 8 + size + (size & 1);
            }
            return null;
        }

        public static bool[] Run(byte[] callerInput, string tempRoot)
        {
            Settings.SavePath = tempRoot;
            Settings.KeybdSoundVolume = 10;
            Settings.MicrophoneOffset = 0;
            SpectreState.DebugMode = false;
            SpectreState.debug_skip_Verification = false;
            ADOBase.conductor = null;
            byte[] original = (byte[])callerInput.Clone();

            AudioRecorder.VerificationResult = true;
            var baseline = new ReplayData
            {
                FileName = "LegacyBaseline",
                KeybdSoundFileName = "cloud.wav",
                KeybdSoundHash = "verified"
            };
            LegacyReplayManagerFixture.TryLoadKeybdSoundFromMemory(baseline, callerInput);
            bool legacyNoSound = SpectreState.HasKeybdSound && WavLoader.loaded_clip != null
                && baseline.KeybdSoundMemory == null
                && !ExportKeybdSound(baseline, out _, out string baselineError)
                && baselineError == "no_sound";

            AudioRecorder.VerificationResult = true;
            WavLoader.FailClipCreation = false;
            var loaded = new ReplayData
            {
                FileName = "CloudReplay",
                KeybdSoundFileName = "cloud.wav",
                KeybdSoundHash = "verified",
                KeybdPrefs = new KeybdPrefs
                {
                    HasVolume = true,
                    Volume = 5,
                    HasOffset = true,
                    OffsetMs = 1
                }
            };
            TryLoadKeybdSoundFromMemory(loaded, callerInput);
            bool loadedAndCached = SpectreState.HasKeybdSound && WavLoader.loaded_clip != null
                && loaded.KeybdSoundMemory != null && loaded.KeybdSoundMemory.SequenceEqual(original);

            callerInput[44] ^= 0x7F;
            bool callerMutationIsolated = loaded.KeybdSoundMemory != null
                && loaded.KeybdSoundMemory.SequenceEqual(original);

            bool exportOk = ExportKeybdSound(loaded, out string savedPath, out _);
            byte[] exported = exportOk ? DataBytes(File.ReadAllBytes(savedPath)) : null;
            bool volumeAndOffsetApplied = exported != null && exported.Length == 6
                && BitConverter.ToInt16(exported, 0) == -500
                && BitConverter.ToInt16(exported, 2) == 1000
                && BitConverter.ToInt16(exported, 4) == -1000;

            KeybdPrefs mutedPrefs = loaded.KeybdPrefs;
            mutedPrefs.IsMuted = true;
            loaded.KeybdPrefs = mutedPrefs;
            loaded.FileName = "CloudReplayMuted";
            bool muteExportOk = ExportKeybdSound(loaded, out string mutedPath, out _);
            byte[] muted = muteExportOk ? DataBytes(File.ReadAllBytes(mutedPath)) : null;
            bool muteApplied = muted != null && muted.Length == 6;
            for (int i = 0; muteApplied && i < muted.Length; i++) muteApplied &= muted[i] == 0;

            AudioRecorder.VerificationResult = false;
            var badHash = new ReplayData
            {
                KeybdSoundFileName = "cloud.wav",
                KeybdSoundHash = "bad",
                KeybdSoundMemory = (byte[])original.Clone()
            };
            TryLoadKeybdSoundFromMemory(badHash, original);
            bool badHashNotCached = badHash.KeybdSoundMemory == null && !SpectreState.HasKeybdSound;

            AudioRecorder.VerificationResult = true;
            WavLoader.FailClipCreation = true;
            var badClip = new ReplayData
            {
                KeybdSoundFileName = "cloud.wav",
                KeybdSoundHash = "verified",
                KeybdSoundMemory = (byte[])original.Clone()
            };
            TryLoadKeybdSoundFromMemory(badClip, original);
            bool badClipNotCached = badClip.KeybdSoundMemory == null && !SpectreState.HasKeybdSound;
            WavLoader.FailClipCreation = false;

            loaded.Reset();
            bool resetClearsCache = loaded.KeybdSoundMemory == null;
            return new[] { legacyNoSound, loadedAndCached, callerMutationIsolated,
                volumeAndOffsetApplied, muteApplied, badHashNotCached, badClipNotCached, resetClearsCache };
        }
    }
    public static class LegacyReplayManagerFixture
    {
        private static void TryLoadKeybdSoundFromBlob(ReplayData data, string path) { }
        __LEGACY_TRY_LOAD__
    }
}
public sealed class CurrentPresetFixture { public int inputOffset; }
public sealed class scrConductor { public static CurrentPresetFixture currentPreset = new CurrentPresetFixture(); }
public static class ADOBase { public static scrConductor conductor; }
""";
            fixture = fixture.Replace("__CACHE_PROPERTY__", cacheProperty, StringComparison.Ordinal)
                .Replace("__RESET_LINE__", resetLine, StringComparison.Ordinal)
                .Replace("__REMOVE_INVALID_PATH_CHARS__", MethodSource("RemoveInvalidPathChars"), StringComparison.Ordinal)
                .Replace("__TRY_LOAD__", MethodSource("TryLoadKeybdSoundFromMemory"), StringComparison.Ordinal)
                .Replace("__LEGACY_TRY_LOAD__", legacyLoad, StringComparison.Ordinal)
                .Replace("__IS_PURE_FILE_NAME__", MethodSource("IsPureFileName"), StringComparison.Ordinal)
                .Replace("__EXPORT__", MethodSource("ExportKeybdSound"), StringComparison.Ordinal);

            string wavTransform = Read(root, @"Features\Replay\Codec\WavAudioTransform.cs");
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("MemoryAudioRepair_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(fixture), CSharpSyntaxTree.ParseText(wavTransform) },
                references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)));

            string tempRoot = Path.Combine(Path.GetTempPath(), "SpectreReplay-AudioRepair-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var type = Assembly.Load(image.ToArray()).GetType("SpectreReplay.Features.Replay.ReplayManagerFixture");
                var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                var results = (bool[])run.Invoke(null, new object[]
                {
                    MakeWav(1000, 1, new short[] { 1000, -1000, 2000, -2000 }, false), tempRoot
                });
                Check(results[0], "AR25 无缓存云端回放且本地无 WAV 时复现旧 no_sound 行为", "");
                Check(results[1], "AR26 实际内存装载验证/clip 成功后缓存 WAV", "");
                Check(results[2], "AR27 调用方后续修改输入数组不污染缓存", "");
                Check(results[3], "AR28 缓存 WAV 导出真实应用回放音量与偏移", "");
                Check(results[4], "AR29 缓存 WAV 导出真实应用回放静音覆盖", "");
                Check(results[5], "AR30 哈希验证失败时清除且不缓存内存 WAV", "");
                Check(results[6], "AR31 clip 创建失败时不缓存内存 WAV", "");
                Check(results[7], "AR32 ReplayData.Reset 清除会话 WAV 缓存", "");
            }
            finally
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Check(false, "AR25-32 实际 ReplayManager WAV 内存导出行为", (ex.InnerException ?? ex).Message);
        }
    }

    private static void OddChunkAndPadding()
    {
        byte[] input = MakeWav(1000, 1, new short[] { 1000, -1000 }, includeOddJunk: true);
        bool ok = WavAudioTransform.TryApply(input, 10, 0, false,
            out byte[] output, out string error);
        Check(ok, "① RIFF odd chunk + padding 可读取并导出", error);
        if (!ok) return;
        Check(Tag(output, 0, "RIFF") && Tag(output, 8, "WAVE"),
            "② 导出头部仍是合法 RIFF/WAVE", "");
        Check(ContainsChunk(output, "JUNK", 3),
            "③ 未知奇数长度块及其 padding 未错位/丢失", "");
        byte[] pcm = FirstData(output);
        Check(pcm != null && pcm.Length == 4 && ReadI16(pcm, 0) == 1000
            && ReadI16(pcm, 2) == -1000,
            "④ odd chunk 后的 PCM 完整保留", "");
    }

    private static void VolumeOffsetMuteStereo()
    {
        // 1000 Hz 下 1 ms 恰好一帧，方便把正/负 offset 方向钉死。
        byte[] input = MakeWav(1000, 2,
            new short[] { 1000, -1000, 2000, -2000, 3000, -3000 }, true);

        bool halfOk = WavAudioTransform.TryApply(input, 5, 1, false,
            out byte[] positive, out string positiveError);
        Check(halfOk, "⑤ 立体声正 offset + 50% volume 变换成功", positiveError);
        if (halfOk)
        {
            byte[] pcm = FirstData(positive);
            Check(pcm != null && pcm.Length == 8
                && ReadI16(pcm, 0) == 1000 && ReadI16(pcm, 2) == -1000
                && ReadI16(pcm, 4) == 1500 && ReadI16(pcm, 6) == -1500,
                "⑥ 正 offset = 裁掉头部，且两声道分别缩放", "");
        }

        bool negativeOk = WavAudioTransform.TryApply(input, 10, -1, false,
            out byte[] negative, out string negativeError);
        Check(negativeOk, "⑦ 立体声负 offset 变换成功", negativeError);
        if (negativeOk)
        {
            byte[] pcm = FirstData(negative);
            Check(pcm != null && pcm.Length == 16
                && ReadI16(pcm, 0) == 0 && ReadI16(pcm, 2) == 0
                && ReadI16(pcm, 4) == 1000 && ReadI16(pcm, 6) == -1000,
                "⑧ 负 offset = 前补静音，不裁掉有效 PCM", "");
        }

        bool muteOk = WavAudioTransform.TryApply(input, 10, 0, true,
            out byte[] muted, out string muteError);
        Check(muteOk, "⑨ 静音覆盖变换成功", muteError);
        if (muteOk)
        {
            byte[] pcm = FirstData(muted);
            bool allZero = pcm != null;
            for (int i = 0; allZero && i < pcm.Length; i++) allZero &= pcm[i] == 0;
            Check(allZero && pcm.Length == 12, "⑩ 静音保持声道/采样率/帧数且 PCM 为零", "");
        }
    }

    private static void MicrophoneFallback()
    {
        bool fallback;
        string a = MicrophoneDevicePolicy.Resolve("old", new[] { "mic-A", "mic-B" }, out fallback);
        Check(a == "mic-A" && fallback, "⑪ 冷启动旧名失效回退第一有效设备", a ?? "null");
        string b = MicrophoneDevicePolicy.Resolve("None", new[] { "mic-A", "mic-B" }, out fallback);
        Check(b == "mic-A" && fallback, "⑫ None 配置回退有效设备", b ?? "null");
        string c = MicrophoneDevicePolicy.Resolve("mic-B", new[] { "mic-A", "mic-B" }, out fallback);
        Check(c == "mic-B" && !fallback, "⑬ 有效设备名保持不变", c ?? "null");
        string d = MicrophoneDevicePolicy.Resolve("mic-A", new[] { "mic-C" }, out fallback);
        Check(d == "mic-C" && fallback, "⑭ 热启动设备变化重新回退", d ?? "null");
        string e = MicrophoneDevicePolicy.Resolve("mic-A", Array.Empty<string>(), out fallback);
        Check(e == null, "⑮ 无设备明确失败，不假成功", "resolved=" + (e ?? "null"));
    }

    private static void LifecycleAndLoadShape()
    {
        string root = Driver.ProjectDir;
        string wav = Read(root, @"State\WavLoader.cs");
        string mgr = Read(root, @"Features\Replay\Runtime\ReplayManager.cs");
        string patch = Read(root, @"Features\Replay\Patches\Patch_Playback.cs");
        string settings = Read(root, @"State\Settings.cs");

        Check(wav.Contains("musicSource.Stop()", StringComparison.Ordinal)
            && wav.Contains("musicSource.clip = null", StringComparison.Ordinal)
            && wav.Contains("Object.Destroy(previous)", StringComparison.Ordinal)
            && wav.Contains("ReplaceLoadedClip", StringComparison.Ordinal),
            "⑯ 替换/卸载先停 source、清引用、Destroy 动态 clip", "");
        Check(patch.Contains("WavLoader.musicSource.volume = effectiveVolume;", StringComparison.Ordinal)
            && patch.Contains("effectiveOffsetMs", StringComparison.Ordinal)
            && patch.Contains("if (offsetChanged ||", StringComparison.Ordinal),
            "⑰ 播放每帧应用 volume，offset 改变强制重定位", "");
        Check(settings.Contains("AudioRecorder.ResolveConfiguredDevice", StringComparison.Ordinal)
            && settings.Contains("microphoneSettingsChanged", StringComparison.Ordinal),
            "⑱ Settings.check 处理旧名/None 并只在变化时保存", "");

        Check(mgr.Contains("WavAudioTransform.TryApply(wav, volumeStep, offsetMs, prefs.IsMuted,", StringComparison.Ordinal)
            && mgr.Contains("wav = adjustedWav;", StringComparison.Ordinal)
            && mgr.Contains("prefs.HasVolume ? prefs.Volume : Settings.KeybdSoundVolume", StringComparison.Ordinal)
            && mgr.Contains("prefs.HasOffset ? prefs.OffsetMs : Settings.MicrophoneOffset", StringComparison.Ordinal),
            "AR22 导出接入有效覆盖设置并写变换后的 WAV", "");

        int loadStart = mgr.IndexOf("internal static bool LoadReplay(out ReplayData replayData,", StringComparison.Ordinal);
        int loadEnd = mgr.IndexOf("/// <summary>\n    /// 只把回放数据读出来", loadStart < 0 ? 0 : loadStart, StringComparison.Ordinal);
        string loadBody = loadStart >= 0 && loadEnd > loadStart ? mgr.Substring(loadStart, loadEnd - loadStart) : "";
        int readCall = loadBody.IndexOf("ReadReplayData(out loaded", StringComparison.Ordinal);
        int stop = loadBody.IndexOf("SpectreState.PlayActions.StopPlaying", StringComparison.Ordinal);
        int clear = loadBody.IndexOf("WavLoader.ClearLoadedClip", StringComparison.Ordinal);
        Check(readCall >= 0 && stop > readCall && clear > stop,
            "⑲ LoadReplay 在解码成功后才停旧会话/替换音频", "");

        int readStart = mgr.IndexOf("internal static bool ReadReplayData(out ReplayData replayData, byte[] raw", StringComparison.Ordinal);
        int readEnd = mgr.IndexOf("internal static bool LoadReplayAuto", readStart < 0 ? 0 : readStart, StringComparison.Ordinal);
        string readBody = readStart >= 0 && readEnd > readStart ? mgr.Substring(readStart, readEnd - readStart) : "";
        Check(!readBody.Contains("StopPlaying", StringComparison.Ordinal)
            && !readBody.Contains("StopRecording", StringComparison.Ordinal),
            "⑳ decoder-only ReadReplayData 无会话副作用", "");
    }

    private static byte[] MakeWav(int rate, int channels, short[] samples, bool includeOddJunk)
    {
        var chunks = new List<(string id, byte[] payload)>();
        byte[] fmt = new byte[16];
        Put16(fmt, 0, 1);
        Put16(fmt, 2, (short)channels);
        Put32(fmt, 4, rate);
        Put32(fmt, 8, rate * channels * 2);
        Put16(fmt, 12, (short)(channels * 2));
        Put16(fmt, 14, 16);
        chunks.Add(("fmt ", fmt));
        if (includeOddJunk) chunks.Add(("JUNK", new byte[] { 9, 8, 7 }));
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) Put16(data, i * 2, samples[i]);
        chunks.Add(("data", data));

        int total = 12;
        foreach (var chunk in chunks) total += 8 + chunk.payload.Length + (chunk.payload.Length & 1);
        byte[] wav = new byte[total];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        Put32(wav, 4, total - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        int cursor = 12;
        foreach (var chunk in chunks)
        {
            Encoding.ASCII.GetBytes(chunk.id).CopyTo(wav, cursor);
            Put32(wav, cursor + 4, chunk.payload.Length);
            Buffer.BlockCopy(chunk.payload, 0, wav, cursor + 8, chunk.payload.Length);
            cursor += 8 + chunk.payload.Length;
            if ((chunk.payload.Length & 1) != 0) wav[cursor++] = 0;
        }
        return wav;
    }

    private static byte[] FirstData(byte[] wav)
    {
        if (wav == null || wav.Length < 12 || !Tag(wav, 0, "RIFF") || !Tag(wav, 8, "WAVE")) return null;
        int end = 8 + (int)Read32(wav, 4);
        for (int p = 12; p + 8 <= end;)
        {
            int size = (int)Read32(wav, p + 4);
            if (size < 0 || p + 8L + size > end) return null;
            if (Tag(wav, p, "data"))
            {
                byte[] data = new byte[size];
                Buffer.BlockCopy(wav, p + 8, data, 0, size);
                return data;
            }
            p += 8 + size + (size & 1);
        }
        return null;
    }

    private static bool ContainsChunk(byte[] wav, string wanted, int wantedSize)
    {
        if (wav == null || wav.Length < 12) return false;
        int end = 8 + (int)Read32(wav, 4);
        for (int p = 12; p + 8 <= end;)
        {
            int size = (int)Read32(wav, p + 4);
            if (size < 0 || p + 8L + size > end) return false;
            if (Tag(wav, p, wanted) && size == wantedSize) return true;
            p += 8 + size + (size & 1);
        }
        return false;
    }

    private static bool Tag(byte[] bytes, int offset, string tag)
        => offset >= 0 && offset + 4 <= bytes.Length
            && bytes[offset] == tag[0] && bytes[offset + 1] == tag[1]
            && bytes[offset + 2] == tag[2] && bytes[offset + 3] == tag[3];

    private static short ReadI16(byte[] bytes, int offset) => (short)Read16(bytes, offset);
    private static ushort Read16(byte[] bytes, int offset)
        => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    private static uint Read32(byte[] bytes, int offset)
        => (uint)(bytes[offset] | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    private static void Put16(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }
    private static void Put32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }
    private static string Read(string root, string relative)
        => File.ReadAllText(Path.Combine(root, relative), Encoding.UTF8);

    private static void Check(bool ok, string label, string detail)
    {
        if (ok) Console.WriteLine("    ✓ " + label);
        else
        {
            _fail++;
            Console.WriteLine("    ✗ " + label + (string.IsNullOrEmpty(detail) ? "" : " —— " + detail));
        }
    }
}
