using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;

namespace UiPreview;

internal static class ShippingCoreCheck
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine("  " + (ok ? "✓ " : "✗ ") + label);
            if (!ok) failed++;
        }
        string Source(string path) => File.ReadAllText(Path.Combine(Driver.ProjectDir, path));
        try
        {
            string config = Source("State/ConfigManager.cs");
            var used = CSharpSyntaxTree.ParseText(config).GetRoot().DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>().Where(m => m.Expression.ToString() == "Settings")
                .Select(m => m.Name.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
            string fields = string.Join("\n", CSharpSyntaxTree.ParseText(Source("State/Settings.cs"))
                .GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(f => f.Declaration.Variables.Any(v => used.Contains(v.Identifier.ValueText)))
                .Select(f => f.ToString()));
            bool[] result = Execute(config, Source("State/AtomicFile.cs"), ConfigFixture.Replace("__FIELDS__", fields));
            string[] labels =
            {
                "SC01 旧扁平RenderOn经完整真实载入/迁移后保持且持久化",
                "SC02 已误迁到Sets的开关能补救，且正确Ints键优先",
                "SC03 启动缺失/无效旧开关保持当前值，重复迁移幂等",
                "SC08 空值/垃圾/未知根结构拒绝并带错误，BOM配置有效",
                "SC09 已知字段坏类型/溢出/非有限数/坏列表均拒绝",
                "SC10 验证现代及旧配置不改Settings/configPath/文件或触发云保存",
                "SC11 旧缺区缺键和未知扩展合法，实际载入保留当前值",
                "SC12 严格读取不存在/空路径/坏JSON返回失败且有错误",
                "SC13 严格预检拒坏分区且无部分设置/路径/磁盘副作用",
                "SC14 启动坏分区隔离，后续合法分区载入且不误报全成功",
                "SC15 实际应用分区抛错可见，错误不被预检成功掩盖",
                "SC16 文件独占锁导致真实读取失败",
                "SC17 仅少量旧扁平键也能识别并应用迁移",
                "SC18 严格旧配置不写盘/改路径，启动迁移落盘失败仍保留已应用值",
                "SC19 本次成功清除上次错误，配置保存输出可被验证",
                "SC20 非空历史Colors只校验对象形状，载入/验证无副作用"
            };
            for (int i = 0; i < result.Length; i++) Check(result[i], labels[i]);
        }
        catch (Exception ex) { Check(false, "SC01-03/08-19 完整真实配置执行: " + (ex.InnerException ?? ex).Message); }
        try
        {
            var root = CSharpSyntaxTree.ParseText(Source("Patch/Shared/SpectrePatchHub.cs")).GetRoot();
            string Method(string name) => root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(m => m.Identifier.ValueText == name).ToString();
            string count = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.ValueText == "ApplyFailureCount").ToString();
            string fixture = HubFixture.Replace("__METHODS__", string.Join("\n", new[]
                { "Initialize", "Resolve", "IsAvailable", "RecordApplyFailure" }.Select(Method)) + "\n" + count);
            bool[] result = Execute(fixture);
            Check(result[0], "SC04 未知版本缺少必要分叉时Replay不可用，而非正常版本跳过");
            Check(result[1], "SC05 真实Initialize把解析失败纳入故障账本与当前计数");
            Check(result[2], "SC06 当前失败计数随恢复更新，不依赖历史账本");
            Check(result[3], "SC07 已知旧版分叉不误报，较新版本仍按实际签名fail-open");
        }
        catch (Exception ex) { Check(false, "SC04-07 真实补丁分支执行: " + (ex.InnerException ?? ex).Message); }
        return failed;
    }

    private static bool[] Execute(params string[] sources)
    {
        string[] paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);
        var refs = paths.Append(typeof(JObject).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ShippingCore_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source)), refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success) throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)));
        return (bool[])Assembly.Load(image.ToArray()).GetType("Fixture").GetMethod("Run").Invoke(null, null);
    }

    private const string ConfigFixture = """"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using SpectreReplay;
public static class Fixture
{
    static bool Valid(string text) => ConfigManager.ValidateConfigData(Encoding.UTF8.GetBytes(text), out _);
    static string path;
    static bool Load(string text, bool current)
    {
        Settings.RenderOn = current;
        File.WriteAllText(path, text);
        ConfigManager.LoadConfigs(path);
        return Settings.RenderOn;
    }
    static string Snapshot() => string.Join("|", typeof(Settings).GetFields().Select(f => f.Name + ":" + Newtonsoft.Json.JsonConvert.SerializeObject(f.GetValue(null))));
    public static bool[] Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "config-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "Configs.json");
        try
        {
            bool first = !Load("""{"RecordingOn":true,"RenderOn":false}""", true)
                && JObject.Parse(File.ReadAllText(path))["Ints"]?["render_on"]?.Value<bool>() == false;
            first &= Load("""{"Fps":60,"RenderOn":true}""", false);
            bool repair = !Load("""{"Sets":{"RenderOn":false},"Ints":{}}""", true);
            repair &= !Load("""{"Sets":{"RenderOn":false}}""", true);
            repair &= Load("""{"Sets":{"RenderOn":false},"Ints":{"render_on":true}}""", false);
            bool safe = !Load("""{"RecordingOn":true}""", false);
            safe &= !Load("""{"RecordingOn":true,"RenderOn":"bad"}""", false);
            Load("""{"RecordingOn":true,"RenderOn":false}""", true);
            string once = File.ReadAllText(path);
            ConfigManager.LoadConfigs(path);
            safe &= File.ReadAllText(path) == once && !Settings.RenderOn;

            bool shape = !ConfigManager.ValidateConfigData(null, out string e) && !string.IsNullOrEmpty(e);
            shape &= !ConfigManager.ValidateConfigData(Array.Empty<byte>(), out e) && !string.IsNullOrEmpty(e);
            foreach (string text in new[] { " ", "garbage", "null", "[]", "{}", "{\"garbage\":1}", "{\"Sets\":[]}", "{\"Ints\":null}", "{\"Colors\":7}", "{\"Sets\":{}} {}" })
                shape &= !ConfigManager.ValidateConfigData(Encoding.UTF8.GetBytes(text), out e) && !string.IsNullOrEmpty(e);
            shape &= !ConfigManager.ValidateConfigData(new byte[] { 0xff, 0xfe, 0xff }, out _);
            shape &= Valid("\uFEFF{\"Sets\":{}}") && Valid("{\"Strings\":{\"savepath\":\"2026-11-26T00:00:00Z\"}}");

            bool types = true;
            foreach (string text in new[] {
                """{"Sets":{"ReplayOn":"true"}}""", """{"Sets":{"RenderOn":1}}""",
                """{"Ints":{"render_fps":"60"}}""", """{"Ints":{"render_fps":999999999999}}""",
                """{"Ints":{"render_tail":1e100}}""", """{"Ints":{"render_tail":"NaN"}}""",
                """{"Ints":{"render_on":null}}""", """{"Strings":{"savepath":7}}""",
                """{"StringLists":{"allowed_keyboard_special_keys":[3]}}""",
                """{"Ints":{"render_overlay_exclude_types":"bad"}}""",
                """{"RecordingOn":"true"}""", """{"Fps":"60"}""", """{"OutputDir":9}""" })
                types &= !Valid(text);

            string before = Snapshot(), oldPath = ConfigManager.configPath;
            string[] files = Directory.GetFiles(dir).OrderBy(f => f).ToArray();
            bool pure = Valid("""{"RecordingOn":false,"RenderOn":true,"Fps":30}""")
                && Valid("""{"Sets":{"ReplayOn":false},"Ints":{"render_fps":30}}""");
            pure &= before == Snapshot() && oldPath == ConfigManager.configPath && once == File.ReadAllText(path)
                && files.SequenceEqual(Directory.GetFiles(dir).OrderBy(f => f)) && SpectreReplay.Features.Cloud.CloudReplay.Saves == 0;

            Settings.RenderOn = false;
            Settings.RenderFps = 73;
            Settings.AutoSave = false;
            Settings.RenderOverlayExcludeTypes = new List<string> { "keep" };
            Settings.AllowedKeyboardSpecialKeys = new HashSet<string> { "old-key" };
            File.WriteAllText(path, """{"Sets":{},"Ints":{},"Colors":{},"Unknown":{"new":1}}""");
            bool missing = ConfigManager.TryLoadConfigs(path, out e) && e == null && !Settings.RenderOn
                && Settings.RenderFps == 73 && !Settings.AutoSave && Settings.AllowedKeyboardSpecialKeys.Contains("old-key")
                && Settings.RenderOverlayExcludeTypes.SequenceEqual(new[] { "keep" });
            bool read = !ConfigManager.TryLoadConfigs(Path.Combine(dir, "absent.json"), out e) && !string.IsNullOrEmpty(e);
            read &= !ConfigManager.TryLoadConfigs(null, out e) && !string.IsNullOrEmpty(e);
            File.WriteAllText(path, "bad json");
            read &= !ConfigManager.TryLoadConfigs(path, out e) && !string.IsNullOrEmpty(e);

            File.WriteAllText(path, """{"Sets":{"ReplayOn":false},"Ints":{"render_fps":"bad"},"Strings":{"savepath":"must-not-apply"}}""");
            before = Snapshot();
            oldPath = ConfigManager.configPath = "sentinel";
            string bad = File.ReadAllText(path);
            bool preflight = !ConfigManager.TryLoadConfigs(path, out e) && !string.IsNullOrEmpty(e)
                && before == Snapshot() && ConfigManager.configPath == oldPath && File.ReadAllText(path) == bad;

            UnityEngine.Debug.Logs.Clear();
            File.WriteAllText(path, """{"Sets":{"ReplayOn":"bad"},"Ints":{"render_fps":97},"Strings":{"savepath":"later"},"StringLists":{"allowed_keyboard_special_keys":["new-key"]}}""");
            ConfigManager.LoadConfigs(path);
            bool isolate = Settings.RenderFps == 97 && Settings.SavePath == "later" && Settings.AllowedKeyboardSpecialKeys.Contains("new-key")
                && UnityEngine.Debug.Logs.Any(s => s.Contains("Sets")) && !UnityEngine.Debug.Logs.Any(s => s.Contains("配置载入成功"));

            File.WriteAllText(path, """{"Sets":{"ReplayOn":true},"Ints":{"render_width":1920},"Strings":{"savepath":"after-clamp"}}""");
            UnityEngine.Mathf.FailClamp = true;
            bool apply = !ConfigManager.TryLoadConfigs(path, out e) && e != null && e.Contains("Ints") && e.Contains("clamp_fault")
                && Settings.SavePath == "after-clamp";
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                read &= !ConfigManager.TryLoadConfigs(path, out e) && !string.IsNullOrEmpty(e);
            bool lockedRead = read;

            File.WriteAllText(path, """{"OutputDir":"legacy-output"}""");
            string legacyText = File.ReadAllText(path);
            oldPath = ConfigManager.configPath = "strict-path-sentinel";
            bool partial = Valid(legacyText) && ConfigManager.TryLoadConfigs(path, out e) && Settings.RenderOutputDir == "legacy-output"
                && File.ReadAllText(path) == legacyText && ConfigManager.configPath == oldPath;
            File.WriteAllText(path, """{"RecordingOn":false,"RenderOn":false}""");
            legacyText = File.ReadAllText(path);
            bool migration;
            UnityEngine.Debug.Logs.Clear();
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                migration = ConfigManager.TryLoadConfigs(path, out e) && e == null && !Settings.ReplayOn && !Settings.RenderOn
                    && ConfigManager.configPath == oldPath && File.ReadAllText(path) == legacyText && !File.Exists(path + ".tmp");
                // 严格预检成功会正确打印载入成功；只检查随后的启动迁移分支，避免旧日志污染断言。
                UnityEngine.Debug.Logs.Clear();
                ConfigManager.LoadConfigs(path);
                migration &= !Settings.ReplayOn && !Settings.RenderOn
                    && UnityEngine.Debug.Logs.Any(s => s.Contains("migration write failed"))
                    && !UnityEngine.Debug.Logs.Any(s => s.Contains("配置载入成功"));
            }
            bool saved = ConfigManager.SaveConfigs(path) && ConfigManager.ValidateConfigData(File.ReadAllBytes(path), out e) && e == null
                && ConfigManager.TryLoadConfigs(path, out e) && e == null;
            File.WriteAllText(path, """{"Colors":{"TextColor":"#ffffff","OldFill":{"r":1},"Palette":[1,2,3]}}""");
            before = Snapshot();
            oldPath = ConfigManager.configPath = "colors-path-sentinel";
            string colorText = File.ReadAllText(path);
            bool colors = Valid(colorText) && ConfigManager.TryLoadConfigs(path, out e) && e == null
                && before == Snapshot() && ConfigManager.configPath == oldPath && File.ReadAllText(path) == colorText;
            return new[] { first, repair, safe, shape, types, pure, missing, read, preflight, isolate, apply, lockedRead, partial, migration, saved, colors };
        }
        finally { Directory.Delete(dir, true); }
    }
}
namespace SpectreReplay
{
    public static class Settings
    {
        __FIELDS__
    }
}
namespace UnityEngine
{
    public static class Debug
    {
        public static readonly List<string> Logs = new();
        public static void Log(string s) => Logs.Add(s);
        public static void LogWarning(string s) => Logs.Add(s);
    }
    public static class Mathf
    {
        public static bool FailClamp;
        public static int Clamp(int n, int a, int b)
        {
            if (FailClamp) { FailClamp = false; throw new InvalidOperationException("clamp_fault"); }
            return Math.Clamp(n, a, b);
        }
        public static float Clamp(float n, float a, float b) => Math.Clamp(n, a, b);
    }
}
namespace SpectreReplay.Features.Cloud
{
    public static class CloudReplay { public static int Saves; public static void OnConfigSaved(string path) { Saves++; } }
}
"""";

    private const string HubFixture = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
sealed class SpectrePatchAttribute : Attribute
{
    public string Feature, ClassName, MethodName;
    public string[] ClassNames, MethodNames;
    public Type ClassType;
    public int MinVersion, MaxVersion;
    public bool AllOverloads;
    public MethodInfo PatchMethod;
}
static class AccessTools { public static Type TypeByName(string n) => null; }
static class AsyncLog { public static void Start() {} }
public static class Fixture
{
    sealed class Spec
    {
        public string Id, Feature, Error, TargetName;
        public string[] FeatureParts;
        public bool FeatureAnd, VersionSkipped;
        public MethodInfo PatchMethod;
        public SpectrePatchAttribute Attr;
        public List<MethodBase> Targets;
    }
    sealed class Expression { public string[] Parts; public bool And, Invalid; }
    static Expression ParseFeatures(string s) => s == null ? null : new Expression { Parts = new[] { s } };
    static readonly object _lock = new object();
    static readonly List<Spec> _specs = new List<Spec>();
    static readonly List<string> ApplyFailures = new List<string>();
    static readonly Dictionary<string, Func<bool>> _featureToggles = new Dictionary<string, Func<bool>>();
    static readonly List<int> _guardedMethods = new List<int>(), _guardStats = new List<int>(), _warnedUnregistered = new List<int>();
    static bool failureSummaryShown, _jalibProbed, _initialRefresh;
    static string _ns;
    static Assembly GameAssembly;
    static int Release, MinSupportedBuild = 141;
    static void UnpatchAll() { _specs.Clear(); }
    static void LogProbe() {}
    static MethodBase FindMethod(Type t, string name, SpectrePatchAttribute a) => t.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
    static List<MethodBase> FindAllOverloads(Type t, string name, SpectrePatchAttribute a) => null;
    __METHODS__
    public static bool[] Run()
    {
        Release = -1;
        Initialize(typeof(Fixture).Assembly);
        bool unknown = !IsAvailable("ReplayOld") && !IsAvailable("ReplayNew")
            && _specs.Where(s => s.Feature == "ReplayOld" || s.Feature == "ReplayNew")
                .All(s => !s.VersionSkipped && s.Targets == null && s.Error != null);
        Release = 150;
        Initialize(typeof(Fixture).Assembly);
        bool ledger = ApplyFailureCount == 1 && ApplyFailures.Count == 1 && !IsAvailable("Missing");
        _specs.Single(s => s.Feature == "Missing").Error = null;
        bool current = ApplyFailureCount == 0;
        Release = 151;
        Initialize(typeof(Fixture).Assembly);
        bool known = IsAvailable("ReplayNew")
            && _specs.Single(s => s.Feature == "ReplayNew") is Spec newBranch
            && !newBranch.VersionSkipped && newBranch.Targets?.Count == 1
            && _specs.Single(s => s.Feature == "ReplayOld").VersionSkipped;
        Release = 149;
        Initialize(typeof(Fixture).Assembly);
        known &= IsAvailable("ReplayOld")
            && _specs.Single(s => s.Feature == "ReplayOld") is Spec oldBranch
            && !oldBranch.VersionSkipped && oldBranch.Targets?.Count == 1
            && _specs.Single(s => s.Feature == "ReplayNew").VersionSkipped;
        return new[] { unknown, ledger, current, known };
    }
}
static class Targets { public static void Found() {} }
static class Patches
{
    [SpectrePatch(Feature = "ReplayOld", MaxVersion = 149, ClassType = typeof(Targets), MethodName = "Found")]
    static void Old() {}
    [SpectrePatch(Feature = "ReplayNew", MinVersion = 150, ClassType = typeof(Targets), MethodName = "Found")]
    static void New() {}
    [SpectrePatch(Feature = "Missing", ClassType = typeof(Targets), MethodName = "Absent")]
    static void Missing() {}
}
""";
}
