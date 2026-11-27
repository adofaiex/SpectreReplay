using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// Execute current product methods under deterministic disk/Steam/Unity boundaries; no account or GUI.
internal static class ShippingCloudCheck
{
    internal static int Run() => Run(Driver.ProjectDir);

    internal static int Run(string projectDir)
    {
        try
        {
            ClassDeclarationSyntax ReadType(string path, string name)
                => CSharpSyntaxTree.ParseText(SrcUtil.Read(projectDir, path)).GetRoot()
                    .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == name);
            var cloud = ReadType("Features/Cloud/CloudReplay.cs", "CloudReplay");
            string[] names = { "OnReplaySaved", "OnConfigSaved", "RestoreConfigManual", "RestoreConfigIfMissing",
                "ApplyRestoredConfig", "RestoreOnStartup", "ScheduleStartupRestore", "BeginConfigRestoreWindow",
                "RestoreWhenReady", "DoRestoreOnce", "BytesEqual", "DownloadTo", "ConfigUploadAllowedNow",
                "CaptureStartupConfigState", "CompleteStartupConfigInitialization", "CancelStartupRestore",
                "StartupRestoreAllowed" };
            string methods = string.Join("\n", cloud.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => names.Contains(m.Identifier.ValueText)).Select(m => m.ToString()));
            string fields = string.Join("\n", cloud.Members.OfType<FieldDeclarationSyntax>()
                .Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText.StartsWith("_config", StringComparison.Ordinal)
                    || v.Identifier.ValueText == "_restoreGeneration" || v.Identifier.ValueText == "_startupRestoreEnabled"))
                .Select(f => f.ToString()));
            var main = ReadType("Main.cs", "Main");
            var load = main.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Load");
            var startup = load.Body.Statements.OfType<TryStatementSyntax>().First().Block.Statements
                .TakeWhile(s => !s.ToString().StartsWith("WavLoader.", StringComparison.Ordinal));
            var presets = ReadType("Features/EffectRemover/EffectPresets.cs", "EffectPresets");
            string presetMethods = string.Join("\n", presets.Members.Where(m => m is PropertyDeclarationSyntax
                || m is FieldDeclarationSyntax || m is MethodDeclarationSyntax method
                && new[] { "Save", "PullFromCloud", "SanitizeName", "IsSafeFileName" }.Contains(method.Identifier.ValueText)));
            var steam = ReadType("Features/Cloud/SteamCloud.cs", "SteamCloud");
            string download = steam.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Download").ToString();
            string atomic = ReadType("State/AtomicFile.cs", "AtomicFile").ToString();
            string helperPath = Path.Combine(projectDir, "Features/EffectRemover/PresetFile.cs");
            string helper = File.Exists(helperPath) ? ReadType("Features/EffectRemover/PresetFile.cs", "PresetFile").ToString() : "";
            var wait = cloud.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "RestoreWhenReady");
            string proxies = (methods.Contains("CaptureStartupConfigState(", StringComparison.Ordinal) ? "internal static void CaptureProxy(string path) => CaptureStartupConfigState(path);" : "internal static void CaptureProxy(string path) {}")
                + (methods.Contains("CompleteStartupConfigInitialization(", StringComparison.Ordinal) ? "internal static void CompleteProxy() => CompleteStartupConfigInitialization();" : "internal static void CompleteProxy() {}");
            proxies += methods.Contains("CancelStartupRestore(", StringComparison.Ordinal) ? "internal static void CancelProxy() => CancelStartupRestore();" : "internal static void CancelProxy() {}";
            bool teardownHook = main.Members.OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == "Teardown").DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(call => call.Expression.ToString().EndsWith("CloudReplay.CancelStartupRestore", StringComparison.Ordinal));
            string source = Fixture.Replace("__TEARDOWN_HOOK__", teardownHook ? "true" : "false").Replace("__PROXIES__", proxies).Replace("__FIELDS__", fields).Replace("__METHODS__", methods)
                .Replace("__STARTUP__", string.Join("\n", startup)).Replace("__PRESETS__", presetMethods)
                .Replace("__DOWNLOAD__", download).Replace("__ATOMIC__", atomic).Replace("__HELPER__", helper)
                .Replace("__WAIT_CALL__", wait.ParameterList.Parameters.Count == 0 ? "RestoreWhenReady()" : "RestoreWhenReady(_restoreGeneration)");
            string trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            var references = trusted.Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(ShippingCloudCheck).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Append(typeof(Newtonsoft.Json.Linq.JObject).Assembly.Location).Distinct()
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("ShippingCloudFixture_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Take(12)));
            string[] lines = (string[])Assembly.Load(image.ToArray()).GetType("SpectreReplay.CloudReplay")
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            foreach (string line in lines) Console.WriteLine("  " + line);
            int fail = lines.Count(l => l.StartsWith("✗", StringComparison.Ordinal));
            Console.WriteLine("  ShippingCloudCheck: assertions=" + lines.Length + " failures=" + fail);
            return fail;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  ✗ ShippingCloudCheck fixture failed: " + (ex.InnerException ?? ex).Message);
            return 1;
        }
    }

    private const string Fixture = """
using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using UnityEngine;
using File = SpectreReplay.FakeFile;
using FileInfo = SpectreReplay.FakeFileInfo;
using Directory = SpectreReplay.FakeDirectory;
using SteamCloud = SpectreReplay.SteamCloud;
using SpectreReplay.Features.EffectRemover;
namespace SpectreReplay {
__ATOMIC__
__HELPER__
static class Main {
    internal sealed class Entry { internal string Path = "mod"; }
    internal static Entry mod = new Entry();
    internal static void LoadInitial(Entry modEntry) { __STARTUP__ }
}
static class Settings {
    internal static bool CloudSyncOn = true, CloudRestoreOnLoad = true, ReplayOn, GetDataFromAsyncInput;
    internal static bool EffectRemoverOn = true;
    internal static string SavePath = "replays";
    internal static void check(string path) {
        if (ConfigManager.NormalizeOnCheck) {
            File.WriteAllBytes(ConfigManager.configPath, Encoding.UTF8.GetBytes("{\"Sets\":{\"ReplayOn\":false}}"));
            CloudReplay.OnConfigSaved(ConfigManager.configPath);
            ConfigManager.NormalizeOnCheck = false;
        }
    }
    internal static bool EffectRemoverFilters;
    internal static bool EffectRemoverAdvancedFilters;
    internal static bool EffectRemoverParticles;
    internal static bool EffectRemoverDecorations;
    internal static bool EffectRemoverBackgrounds;
    internal static bool EffectRemoverCameras;
    internal static bool EffectRemoverSetCameraZoom;
    internal static bool EffectRemoverPlanetOrbit;
    internal static bool EffectRemoverPlanetScale;
    internal static bool EffectRemoverPlanetRadius;
    internal static bool EffectRemoverRepeatEvents;
    internal static bool EffectRemoverFrameRate;
    internal static bool EffectRemoverHitSounds;
    internal static bool EffectRemoverCheckpoints;
    internal static bool EffectRemoverInputEvents;
    internal static bool EffectRemoverHoldSounds;
    internal static bool EffectRemoverTrackAnimations;
    internal static bool EffectRemoverTrackPositions;
    internal static bool EffectRemoverTrackMoves;
    internal static bool EffectRemoverTrackColors;
    internal static bool EffectRemoverHideIcons;
    internal static bool EffectRemoverRemoveAllDecorations;
    internal static bool EffectRemoverResetTrackOpacity;
    internal static bool EffectRemoverResetTrackAnimation;
    internal static bool EffectRemoverResetTrackColor;
    internal static float EffectRemoverCameraZoomScale;
}
static class ConfigManager {
    internal static string configPath = "mod/Configs.json";
    internal static bool NormalizeOnCheck, LoadFails;
    internal static void LoadConfigs(string path) { configPath = path; if (LoadFails) throw new IOException("load failed"); if (File.Exists(path)) Settings.EffectRemoverOn = false; }
    internal static bool TryLoadConfigs(string path, out string error) { error = LoadFails ? "load_failed" : null; if (LoadFails) return false; LoadConfigs(path); return true; }
    internal static bool ValidateConfigData(byte[] data, out string error) {
        try { var j = JObject.Parse(Encoding.UTF8.GetString(data)); if (j["Sets"] is not JObject) throw new Exception(); error = null; return true; }
        catch { error = "invalid_config"; return false; }
    }
}
static class MainThread {
    internal static bool IsMainThread = true;
    internal static IEnumerator Last;
    internal static object StartCoroutine(IEnumerator routine) { Last = routine; return new object(); }
    internal static void Run(Action action) => action();
}
static class SpectreState {
    internal static class PlayActions { internal static void StopPlaying() {} internal static void StopRecording() {} }
    internal static void SetGetKeyAsyncEnabled(bool enabled) {}
}
static class FakeDirectory { internal static bool Exists(string path) => true; internal static void CreateDirectory(string path) {} }
sealed class FakeFileInfo { readonly string path; internal FakeFileInfo(string path) { this.path = path; } internal long Length => FakeFile.ReadAllBytes(path).Length; }
static class FakeFile {
    internal static Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    internal static bool FailWrite, FailCopy;
    internal static string PublishRace;
    internal static string Key(string p) => System.IO.Path.GetFullPath(p).Replace('\\', '/');
    internal static bool Exists(string p) => p != null && Data.ContainsKey(Key(p));
    internal static byte[] ReadAllBytes(string p) => Data[Key(p)].ToArray();
    internal static string ReadAllText(string p) => Encoding.UTF8.GetString(ReadAllBytes(p));
    internal static void WriteAllBytes(string p, byte[] b) { Data[Key(p)] = FailWrite ? new byte[] { 7 } : b.ToArray(); if (FailWrite) throw new IOException("disk full"); if (PublishRace != null) { Data[Key(PublishRace)] = Encoding.UTF8.GetBytes("racing local preset"); PublishRace = null; } }
    internal static void WriteAllText(string p, string s, Encoding encoding = null) => WriteAllBytes(p, (encoding ?? Encoding.UTF8).GetBytes(s));
    internal static void Copy(string from, string to, bool overwrite) { if (FailCopy && to.EndsWith(".bak", StringComparison.Ordinal) && !to.EndsWith(".rolling.bak", StringComparison.Ordinal)) throw new IOException("backup denied"); if (!overwrite && Exists(to)) throw new IOException("exists"); Data[Key(to)] = ReadAllBytes(from); }
    internal static void Move(string from, string to) { if (Exists(to)) throw new IOException("exists"); Data[Key(to)] = ReadAllBytes(from); Delete(from); }
    internal static void Replace(string from, string to, string bak) { if (!Exists(to)) throw new IOException("missing"); if (bak != null) Copy(to, bak, true); Data[Key(to)] = ReadAllBytes(from); Delete(from); }
    internal static void Delete(string p) => Data.Remove(Key(p));
}
static class NativeDownload {
    static IntPtr _iface = new IntPtr(1);
    const int MaxFileBytes = 100 * 1024 * 1024;
    static void Ensure() {}
    static IntPtr ToUtf8(string name) => Marshal.StringToHGlobalAnsi(name);
    static bool SteamAPI_ISteamRemoteStorage_FileExists(IntPtr iface, IntPtr name) => SteamCloud.Data.ContainsKey(Marshal.PtrToStringAnsi(name));
    static int SteamAPI_ISteamRemoteStorage_GetFileSize(IntPtr iface, IntPtr name) => SteamCloud.Data.TryGetValue(Marshal.PtrToStringAnsi(name), out var b) ? b.Length : 0;
    static int SteamAPI_ISteamRemoteStorage_FileRead(IntPtr iface, IntPtr name, byte[] bytes, int size) { var b = SteamCloud.Data[Marshal.PtrToStringAnsi(name)]; Array.Copy(b, bytes, b.Length); return b.Length; }
    __DOWNLOAD__
}
static class SteamCloud {
    internal const string RemoteRoot = "SpectreReplay", RemoteReplayDir = RemoteRoot + "/Replays";
    internal static bool Ready = true;
    internal static string Reason = "stub";
    internal struct Entry { internal string Name; }
    internal static Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
    internal static int Downloads;
    internal static bool Exists(string name) => Data.ContainsKey(name);
    internal static bool UploadFile(string name, string path, out string error) { error = null; Data[name] = File.ReadAllBytes(path); return true; }
    internal static bool Upload(string name, byte[] bytes, out string error) { error = null; Data[name] = bytes.ToArray(); return true; }
    internal static bool Download(string name, out byte[] bytes, out string error) { Downloads++; return NativeDownload.Download(name, out bytes, out error); }
    internal static List<Entry> List(string prefix) => Data.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => new Entry { Name = k }).ToList();
}
static class ReplayManager {
    internal static bool ReadReplayData(out object data, byte[] bytes, string name) { data = null; return bytes != null && bytes.SequenceEqual(new byte[] { 1, 2, 3 }); }
}
static class CloudReplay {
    const int MaxWavBytes = 48 * 1024 * 1024;
    const string RemoteConfigName = "SpectreReplay/Configs.json";
    __FIELDS__
    static string LocalConfigPath() => ConfigManager.configPath;
    static string RemoteNameOf(string name) => SteamCloud.RemoteReplayDir + "/" + name;
    static void InvalidateStats() {}
    static bool UploadConfigBytes(string path, byte[] b) => ConfigUploadAllowedNow();
    internal struct RestoreResult { internal int Restored, Failed; internal string LastError; }
    static RestoreResult RestoreMissing() { SteamCloud.Downloads++; return new RestoreResult(); }
    __METHODS__
    __PROXIES__
    static readonly List<string> Results = new List<string>();
    static void Expect(bool ok, string name) => Results.Add((ok ? "✓ " : "✗ ") + name);
    static bool RemoteIs(string name, byte first) => SteamCloud.Data.TryGetValue(RemoteNameOf(name), out var bytes) && bytes.Length > 0 && bytes[0] == first;
    static byte[] Config = Encoding.UTF8.GetBytes("{\"Sets\":{\"ReplayOn\":true}}");
    static void Reset() {
        File.Data.Clear(); SteamCloud.Data.Clear(); File.FailWrite = File.FailCopy = false; File.PublishRace = null;
        _configRestoreWindowActive = _configRestoreScheduled = _configRestoreSettled = _configLocalMissingAtStartup = false;
        _configPending = false; _configUploaded = null;
        Settings.CloudSyncOn = Settings.CloudRestoreOnLoad = true; Settings.EffectRemoverOn = true;
        ConfigManager.configPath = Path.Combine("mod", "Configs.json"); ConfigManager.LoadFails = ConfigManager.NormalizeOnCheck = false;
        SteamCloud.Ready = true; SteamCloud.Downloads = 0; MainThread.Last = null;
        Features.EffectRemover.EffectRemover.Restores = Features.EffectRemover.EffectRemover.Buttons = Ui.OnGui.Refreshes = 0;
    }
    public static string[] Run() {
        Reset(); ConfigManager.NormalizeOnCheck = true; SteamCloud.Data[RemoteConfigName] = Config;
        Main.LoadInitial(new Main.Entry()); RestoreOnStartup();
        Expect(_configLocalMissingAtStartup && !_configRestoreSettled, "F01 early snapshot survives normalization save");
        Expect(RestoreConfigIfMissing() && File.ReadAllBytes(LocalConfigPath()).SequenceEqual(Config), "F01 generated default is replaced by cloud configuration");
        Reset(); SteamCloud.Data[RemoteConfigName] = Config; Main.LoadInitial(new Main.Entry());
        File.WriteAllText(LocalConfigPath(), "local edit"); OnConfigSaved(LocalConfigPath()); RestoreOnStartup();
        Expect(!RestoreConfigIfMissing() && !ConfigUploadAllowedNow() && File.ReadAllText(LocalConfigPath()) == "local edit", "F01 late user save remains conflict locked after re-enable");
        Reset(); File.WriteAllText(LocalConfigPath(), "existing"); Main.LoadInitial(new Main.Entry()); RestoreOnStartup(); SteamCloud.Data[RemoteConfigName] = Config;
        Expect(!RestoreConfigIfMissing() && File.ReadAllText(LocalConfigPath()) == "existing", "F01 existing configuration is never overwritten");
        Reset(); File.WriteAllBytes("body.sprp", new byte[] { 1, 2, 3 }); File.WriteAllBytes("body.wav", new byte[] { 5 });
        SteamCloud.Data[RemoteNameOf("body.sprp")] = new byte[] { 8 }; SteamCloud.Data[RemoteNameOf("body.wav")] = new byte[] { 9 };
        OnReplaySaved("body.sprp", "body.wav");
        Expect(RemoteIs("body.sprp", 8) && RemoteIs("body.wav", 9), "F02 same-name body and WAV retain first arrival");
        SteamCloud.Data.Remove(RemoteNameOf("body.wav")); OnReplaySaved("body.sprp", "body.wav");
        Expect(RemoteIs("body.sprp", 8) && RemoteIs("body.wav", 5), "F02 missing WAV fills independently of existing body");
        SteamCloud.Data.Remove(RemoteNameOf("body.sprp")); OnReplaySaved("body.sprp", "body.wav");
        Expect(RemoteIs("body.sprp", 1) && RemoteIs("body.wav", 5), "F02 missing body fills independently of existing WAV");
        foreach (string bad in new[] { "not json", "[]", "{\"Sets\":[]}", "{\"unrelated\":true}" }) {
            Reset(); File.WriteAllText(LocalConfigPath(), "original"); SteamCloud.Data[RemoteConfigName] = Encoding.UTF8.GetBytes(bad);
            bool ok = RestoreConfigManual(out _);
            Expect(!ok && File.ReadAllText(LocalConfigPath()) == "original", "F03 invalid cloud config leaves local unchanged: " + bad);
        }
        Reset(); File.WriteAllText(LocalConfigPath(), "original"); File.FailCopy = true; SteamCloud.Data[RemoteConfigName] = Config;
        Expect(!RestoreConfigManual(out _) && File.ReadAllText(LocalConfigPath()) == "original", "F03 backup failure aborts before replacement");
        Reset(); File.WriteAllText(LocalConfigPath(), "original"); ConfigManager.LoadFails = true; SteamCloud.Data[RemoteConfigName] = Config;
        Expect(!RestoreConfigManual(out _) && !_configRestoreSettled, "F03 failed application is not reported as successful restore");
        Reset(); SteamCloud.Download("absent", out var missing, out var missingError);
        Expect(missing == null && missingError == "not_found", "F04 native missing file differs from legitimate empty file");
        SteamCloud.Data["empty"] = Array.Empty<byte>();
        Expect(SteamCloud.Download("empty", out var empty, out _) && empty.Length == 0, "F04 generic cloud storage permits legitimate empty file");
        Expect(!DownloadTo("absent.sprp", "missing.sprp", out _) && !File.Exists("missing.sprp"), "F04 missing download never publishes local replay");
        SteamCloud.Data[RemoteNameOf("empty.sprp")] = Array.Empty<byte>();
        Expect(!DownloadTo("empty.sprp", "empty.sprp", out _) && !File.Exists("empty.sprp"), "F04 empty replay is rejected before local publication");
        SteamCloud.Data[RemoteNameOf("bad.sprp")] = new byte[] { 99 };
        Expect(!DownloadTo("bad.sprp", "bad.sprp", out _) && !File.Exists("bad.sprp"), "F04 malformed replay is rejected before local publication");
        Reset(); string preset = Path.Combine("mod", "EffectPresets", "p.json"); File.WriteAllText(preset, "original"); File.FailWrite = true;
        var save = EffectPresets.Save("p");
        Expect(!save.Ok && File.ReadAllText(preset) == "original", "F05 interrupted preset save retains old target");
        File.FailWrite = false; File.Delete(preset); SteamCloud.Data[EffectPresets.CloudPrefix + "p.json"] = Config; File.FailWrite = true;
        var pull = EffectPresets.PullFromCloud();
        Expect(pull.Pulled == 0 && !File.Exists(preset), "F05 failed pull leaves no published half-file");
        File.FailWrite = false; pull = EffectPresets.PullFromCloud();
        Expect(pull.Pulled == 1 && File.ReadAllBytes(preset).SequenceEqual(Config), "F05 later pull retries missing preset successfully");
        SteamCloud.Data[EffectPresets.CloudPrefix + "p.json"] = new byte[] { 8 }; pull = EffectPresets.PullFromCloud();
        Expect(pull.Pulled == 0 && File.ReadAllBytes(preset).SequenceEqual(Config), "F05 same-name local preset is never overwritten by pull");
        Reset(); SteamCloud.Data[EffectPresets.CloudPrefix + "p.json"] = Config; File.PublishRace = preset;
        pull = EffectPresets.PullFromCloud();
        Expect(pull.Pulled == 0 && File.ReadAllText(preset) == "racing local preset", "F05 publish race retains independently created same-name local preset");
        foreach (bool sync in new[] { true, false }) {
            Reset(); Main.LoadInitial(new Main.Entry()); SteamCloud.Ready = false; RestoreOnStartup(); var wait = MainThread.Last;
            wait.MoveNext(); if (sync) Settings.CloudSyncOn = false; else Settings.CloudRestoreOnLoad = false;
            SteamCloud.Ready = true; int before = SteamCloud.Downloads; wait.MoveNext();
            Expect(SteamCloud.Downloads == before, "F06 waiting restore cancels when " + (sync ? "CloudSyncOn" : "CloudRestoreOnLoad") + " turns off");
            Expect(!ConfigUploadAllowedNow(), "F01/F06 cancellation does not unlock unread cloud configuration");
        }
        Reset(); Main.LoadInitial(new Main.Entry()); SteamCloud.Ready = false; RestoreOnStartup(); var disabled = MainThread.Last;
        disabled.MoveNext(); CancelProxy(); SteamCloud.Ready = true; int disabledBefore = SteamCloud.Downloads; disabled.MoveNext();
        Expect(SteamCloud.Downloads == disabledBefore, "F06 disabled mod cancels outstanding restore session");
        Expect(__TEARDOWN_HOOK__, "F06 Main teardown calls restore cancellation before coroutine pump removal");
        Reset(); Main.LoadInitial(new Main.Entry()); SteamCloud.Ready = false; RestoreOnStartup(); var stale = MainThread.Last;
        stale.MoveNext(); RestoreOnStartup(); SteamCloud.Ready = true; int staleBefore = SteamCloud.Downloads; stale.MoveNext();
        Expect(SteamCloud.Downloads == staleBefore, "F06 stale coroutine cannot restore in a newer enable generation");
        Reset(); SteamCloud.Data[RemoteConfigName] = Config; File.WriteAllText(LocalConfigPath(), "original");
        bool restored = RestoreConfigManual(out _);
        Expect(restored && File.ReadAllText(LocalConfigPath() + ".bak") == "original", "F03 successful application retains pre-restore backup");
        Expect(restored && Features.EffectRemover.EffectRemover.Restores == 1 && Features.EffectRemover.EffectRemover.Buttons == 1,
            "F07 restored effect-off uses guarded level restore and save-button path");
        Expect(restored && Ui.OnGui.Refreshes == 1, "F03 successful restore resets stale UI configuration drafts");
        return Results.ToArray();
    }
}
}
namespace SpectreReplay.Features.Cloud { static class CloudReplay {
    internal static void CaptureStartupConfigState(string path) => SpectreReplay.CloudReplay.CaptureProxy(path);
    internal static void CompleteStartupConfigInitialization() => SpectreReplay.CloudReplay.CompleteProxy();
} }
namespace SpectreReplay.Features.EffectRemover {
static class EffectPresets { __PRESETS__ }
static class EffectRemover { internal static int Restores, Buttons; internal static void RestoreOriginalLevelData() { Restores++; } internal static void RestoreEditorSaveButtons() { Buttons++; } internal static void RefreshEditorSaveButtons() {} }
}
namespace SpectreReplay.Features.VideoRender { static class VideoRender { internal static bool CaptureInProgress; internal static void CancelRender() {} } }
namespace SpectreReplay.PatchShared { static class SpectrePatchHub { internal static void Refresh() {} } }
namespace SpectreReplay.Ui { static class OnGui { internal static int Refreshes; internal static void RefreshAfterConfigRestore() { Refreshes++; } } }
namespace UnityEngine { static class Debug { internal static void Log(string s) {} internal static void LogWarning(string s) {} } static class Time { internal static float realtimeSinceStartup; } sealed class WaitForSecondsRealtime { internal WaitForSecondsRealtime(float seconds) {} } }
""";
}
