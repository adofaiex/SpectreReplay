using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// 只执行产品真方法/真分支；引擎、目录框及云端均为无副作用替身，不是视觉验收。
internal static class ShippingUiCheck
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label)
        {
            Console.WriteLine("  " + (ok ? "✓ " : "✗ ") + label);
            if (!ok) failed++;
        }
        void Execute(Func<bool[]> test, string[] labels)
        {
            try
            {
                bool[] results = test();
                for (int i = 0; i < labels.Length; i++) Check(results[i], labels[i]);
            }
            catch (Exception ex)
            {
                Check(false, string.Join(" / ", labels) + ": " + (ex.InnerException ?? ex).Message);
            }
        }
        Execute(() =>
        {
            string[] fields = { "_outDir", "_outDirLoaded", "_outDirBaseline", "_pickOutDirPending", "_outDirPickVersion", "_openRow", "_openSeq", "_capturing", "_tab", "_subSel", "TabKeys", "SubTabKeys", "_fields", "_fieldCommitters", "_fieldValues", "_sliderDrafts", "FieldW", "InlineLabelW", "LabelW" };
            string[] methods = { "GetField", "SyncField", "TryParse", "NumberOf", "UnitOf", "CommitText", "RefreshAfterConfigRestore", "CancelPendingInteraction", "HandleEscape", "CancelText", "RegisterSliderField", "DrawSliderField", "RowSlider", "ModernSlider", "RowDropdown", "RowInput", "QueueOutDirPick", "PickOutDirDeferred", "SetSubTab" };
            var members = Class("Ui/OnGui.cs", "OnGui").Members.Where(m =>
                m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => fields.Contains(v.Identifier.ValueText))
                || m is MethodDeclarationSyntax method && methods.Contains(method.Identifier.ValueText)
                || m is PropertyDeclarationSyntax p && new[] { "CurrentTab", "CapturingTarget", "PatchFailures", "PatchFailuresPublic" }.Contains(p.Identifier.ValueText)
                || m is FieldDeclarationSyntax pf && pf.Declaration.Variables.Any(v => v.Identifier.ValueText == "PatchFailures")
                || m is ClassDeclarationSyntax c && c.Identifier.ValueText == "SliderDraft");
            var window = Class("Ui/ModernSettingsWindow.cs", "ModernSettingsWindow");
            var escape = window.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Update")
                .DescendantNodes().OfType<IfStatementSyntax>().Single(s => s.Condition.ToString().Contains("Input.GetKeyDown(KeyCode.Escape)", StringComparison.Ordinal));
            var close = window.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "SetOpen")
                .DescendantNodes().OfType<IfStatementSyntax>().Single(s => s.Condition.ToString() == "_open").Statement;
            string pageMethods = string.Join("\n", new[] { "Pages/Pages.cs", "Pages/RecordPage.cs" }.SelectMany(p => Class(p, "Pages").Members)
                .Where(m => m is MethodDeclarationSyntax method && new[] { "CancelCaptures", "RefreshAfterConfigRestore" }.Contains(method.Identifier.ValueText)
                    || m is PropertyDeclarationSyntax prop && prop.Identifier.ValueText == "KeyRemapCapturing"));
            string code = CoreFixture.Replace("__MEMBERS__", string.Join("\n", members), StringComparison.Ordinal)
                .Replace("__ESCAPE__", escape.ToString(), StringComparison.Ordinal)
                .Replace("__CLOSE__", close.ToString(), StringComparison.Ordinal)
                .Replace("__PAGE_METHODS__", pageMethods, StringComparison.Ordinal);
            return RunFixture(code);
        }, new[] {
            "SU16 live补丁故障计数随Hub变化且兼容Main旧赋值，不残留旧数",
            "SU22 恢复B取代未编辑/正在编辑A草稿，关窗不写回A且取消旧捕获",
            "SU23 一级/二级切页撤热键、重映射和下拉捕获，Escape先取消再可关窗",
            "SU25 两种滑条热键关窗解析夹取，仅提交一次、非法值不提交、Escape丢弃",
            "SU28 两种布局Dim下已展开下拉/输出目录不回调，也不排原生对话框"
        });
        Execute(() =>
        {
            var body = Method("Ui/ModernSettingsWindow.cs", "DrawBody");
            string prefix = string.Join("\n", body.Body.Statements.TakeWhile(s => s is not LocalDeclarationStatementSyntax d || !d.Declaration.Variables.Any(v => v.Identifier.ValueText == "bottom")));
            var sub = Method("Ui/ModernSettingsWindow.cs", "DrawSubTabs");
            var height = Class("Ui/ModernSettingsWindow.cs", "ModernSettingsWindow").Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "SubTabsHeight");
            return RunFixture(LayoutFixture.Replace("__PREFIX__", prefix, StringComparison.Ordinal)
                .Replace("__METHODS__", sub + "\n" + string.Join("\n", height), StringComparison.Ordinal));
        }, new[] { "SU26 故障0/非0导航容器与正文偏移分别容纳34/66px，只绘制一份导航" });
        Execute(() =>
        {
            var methods = new[] { "LoadReplayFile", "LoadReplayFromCloud", "NotifyReplayLoaded" }
                .SelectMany(n => Class("Pages/PlaybackPage.cs", "Pages").Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == n));
            return RunFixture(PlaybackFixture.Replace("__METHODS__", string.Join("\n", methods), StringComparison.Ordinal));
        }, new[] { "SU24 本地/云端装载在总开关off时明确未启用（包括缺键音），不偷偷开启" });
        Execute(() => RunFixture(CloudFixture.Replace("__METHOD__", Method("Pages/CloudPanel.cs", "QueueCloudAction").ToString(), StringComparison.Ordinal)),
            new[] { "SU29 上传/恢复部分成功同时汇报成功数、失败数、错误，采用警告级别" });
        return failed;
    }

    private static ClassDeclarationSyntax Class(string path, string name)
        // SrcUtil 的 Pages/Pages.cs 是整组 partial 聚合选择器；这里需要指定文件的真类体。
        => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Driver.ProjectDir, path))).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c => c.Identifier.ValueText == name);
    private static MethodDeclarationSyntax Method(string path, string name)
        => Class(path, path.StartsWith("Pages/", StringComparison.Ordinal) ? "Pages" : "ModernSettingsWindow")
            .Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
    private static bool[] RunFixture(string code)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
            .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ShippingUi_" + Guid.NewGuid().ToString("N"), new[] { CSharpSyntaxTree.ParseText(code) },
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        if (!result.Success) throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)));
        return (bool[])Assembly.Load(image.ToArray()).GetType("Fixture").GetMethod("Run").Invoke(null, null);
    }

    private const string CoreFixture = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using OnGui = Fixture;
public static class Fixture
{
    __MEMBERS__
    static void DrawModernTextStack(string key) { }
    static void RowGap() { }
    static object _unused;
    static void InvokeOptional(string method) => typeof(Fixture).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null);
    static void Escape() { __ESCAPE__ }
    static bool closed, _open, _closing;
    static void SetOpen(bool value) { closed = !value; if (!value) __CLOSE__ }
    static TextField Field(string key) => GetField(key);
    static string Fmt(float value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static bool[] Run()
    {
        PatchShared.SpectrePatchHub.ApplyFailureCount = 1; PatchFailures = 1;
        PatchShared.SpectrePatchHub.ApplyFailureCount = 4;
        bool live = PatchFailuresPublic == 4;
        PatchShared.SpectrePatchHub.ApplyFailureCount = 0; PatchFailures = 99; live &= PatchFailuresPublic == 0;
        bool restore = true;
        foreach (bool editing in new[] { false, true })
        {
            _outDir.Text = editing ? "A-edited" : "A"; _outDir.Editing = editing; _outDirLoaded = true;
            typeof(Fixture).GetField("_outDirBaseline", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, "A");
            float restoredNumber = 10; RowSlider("restore-number", restoredNumber, 1, 100, Fmt, v => restoredNumber = v);
            Field("restore-number").Text = "99"; Field("restore-number").Editing = true;
            restoredNumber = 20;
            _capturing = 7; _openRow = 2; Pages.Pages._remapRow = 1; _pickOutDirPending = true;
            Settings.RenderOutputDir = "B";
            InvokeOptional("RefreshAfterConfigRestore"); CommitText();
            restore &= restoredNumber == 20 && _outDir.Text == "B" && Settings.RenderOutputDir == "B" && !_outDir.Editing && _capturing == 0 && _openRow == -1 && Pages.Pages._remapRow == -1 && !_pickOutDirPending;
        }
        bool navigation = true;
        _capturing = 7; _openRow = 2; Pages.Pages._remapRow = 1; CurrentTab = 1;
        navigation &= _capturing == 0 && _openRow == -1 && Pages.Pages._remapRow == -1;
        _capturing = 8; _openRow = 3; Pages.Pages._remapRow = 2; SetSubTab(1);
        navigation &= _capturing == 0 && _openRow == -1 && Pages.Pages._remapRow == -1;
        closed = false; _capturing = 9; Escape(); navigation &= _capturing == 0 && !closed;
        Escape(); navigation &= closed;
        bool sliders = true;
        foreach (bool modern in new[] { false, true })
        {
            Gui.ModernMode = modern; Gui.IsDim = false; TextField.Commit = false;
            float setting = 10; int calls = 0; string key = modern ? "modern" : "classic";
            Action<float> change = v => { setting = v; calls++; };
            RowSlider(key, setting, 1, 100, Fmt, change); Field(key).Text = "999"; Field(key).Editing = true;
            SetOpen(false); CommitText(); sliders &= setting == 100 && calls == 1;
            RowSlider(key, setting, 1, 100, Fmt, change); Field(key).Text = "NaN"; Field(key).Editing = true;
            CommitText(); sliders &= setting == 100 && calls == 1;
            Field(key).Text = "1"; Field(key).Editing = true; closed = false; Escape(); CommitText();
            sliders &= setting == 100 && calls == 1 && !Field(key).Editing;
            // Draw提交后不能在关窗时再调一次旧回调；拖动后旧value也不能被当作真值。
            TextField.Commit = true; Field(key).Text = "50"; Field(key).Editing = true;
            RowSlider(key, setting, 1, 100, Fmt, change); CommitText();
            sliders &= setting == 50 && calls == 2;
            TextField.Commit = false;
            RowSlider(key, setting, 1, 100, Fmt, change); Field(key).Text = "－5"; Field(key).Editing = true;
            SetOpen(false); sliders &= setting == 1 && calls == 3;
            int oldCalls = calls;
            Gui.NextSlider = 20; RowSlider(key, setting, 1, 100, Fmt, change);
            Field(key).Text = "20"; Field(key).Editing = true; CommitText();
            sliders &= setting == 20 && calls == oldCalls + 1;
        }
        bool dim = true;
        foreach (bool modern in new[] { false, true })
        {
            Gui.ModernMode = modern; Gui.IsDim = true; GUILayout.Click = true; TextField.Commit = true;
            _openSeq = 0; _openRow = 0; int calls = 0;
            GUILayout.Hits = new Queue<bool>(new[] { false, true, true });
            RowDropdown("dd", new[] { "A", "B" }, 0, _ => calls++);
            GUILayout.Hits.Clear();
            int queued = MainThread.Queued; Settings.RenderOutputDir = "B"; _outDir.Text = "A"; _outDir.Editing = true;
            RowInput("out"); CommitText();
            dim &= calls == 0 && _openRow == -1 && MainThread.Queued == queued && Settings.RenderOutputDir == "B";
        }
        Gui.ModernMode = true; Gui.IsDim = false; TextField.Commit = false; GUILayout.Click = true;
        Settings.RenderOn = true; RowInput("out");
        Settings.RenderOutputDir = "B"; InvokeOptional("RefreshAfterConfigRestore");
        int picks = FileBrowser.Calls; MainThread.RunPending();
        restore &= FileBrowser.Calls == picks && Settings.RenderOutputDir == "B";
        Settings.RenderOn = true; RowInput("out"); Settings.RenderOn = false;
        MainThread.RunPending(); dim &= FileBrowser.Calls == picks;
        return new[] { live, restore, navigation, sliders, dim };
    }
}
static class Settings { public static string RenderOutputDir = "", SavePath = ""; public static bool RenderOn = true; public static void CommitSafe() { } }
static class Loc { public static string T(string key) => key; public static void RefreshLanguage() { } }
static class MainThread { public static int Queued; static List<Action> Pending = new(); public static void Defer(Action action) { Queued++; Pending.Add(action); } public static void RunPending() { var actions=Pending.ToArray(); Pending.Clear(); foreach(var action in actions) action(); } }
static class FileBrowser { public static int Calls; public static string PickFolder(string a, object b, object c, string d) { Calls++; return "A-choice"; } }
static class Debug { public static void LogWarning(string text) { } }
static class Input { public static bool GetKeyDown(KeyCode code) => true; }
static class UiInputBlock { public static void Clear() { } }
enum KeyCode { Escape }
static class Mathf { public static bool Approximately(float a, float b) => Math.Abs(a-b) < 0.00001; public static float Clamp(float v, float a, float b) => Math.Clamp(v,a,b); public static int Clamp(int v, int a, int b) => Math.Clamp(v,a,b); }
class GUIStyle { }
struct Rect { public float x,y,width,height; public float xMax => x+width; public Rect(float x,float y,float w,float h) { this.x=x;this.y=y;width=w;height=h; } }
static class Gui
{
    public static bool ModernMode, IsDim;
    public static GUIStyle LabelStyle = new(), ButtonStyle = new(), ModernDescStyle = new(), AccentButtonStyle = new();
    public const float LabelW=200; public static float ModernLabelW, ModernValueW, ModernDropdownW, ModernDropdownListW, CtrlW, ModernInputW;
    public static void Dim(bool dim, Action draw) { bool old=IsDim; IsDim |= dim; try { draw(); } finally { IsDim=old; } }
    public static float? NextSlider;
    public static float Slider(Rect r,float value,float min,float max) { float result = NextSlider ?? value; NextSlider = null; return result; }
    public static void Text(Rect r,string s,object color,object style) { }
    public static object DimCol, SmallStyle;
    public static void Tip(Rect r,string s) { }
    public static void BeginModernRow() { }
    public static void EndModernRow() { }
    public static float ButtonW(string text,GUIStyle style,float w) => w;
}
static class GUILayout
{
    public static bool Click;
    public static Queue<bool> Hits = new(); public static bool NextHit() => Hits.Count > 0 ? Hits.Dequeue() : Click;
    public static object Width(float f)=>null; public static object Height(float f)=>null; public static object ExpandWidth(bool b)=>null;
    public static void BeginHorizontal() { } public static void EndHorizontal() { } public static void BeginVertical(params object[] args) { } public static void EndVertical() { }
    public static void Label(string s,GUIStyle style,params object[] args) { }
    public static bool Button(string s,GUIStyle style,params object[] args) => NextHit();
    public static void Space(float f) { } public static void FlexibleSpace() { }
}
static class GUILayoutUtility { public static Rect GetRect(float a,float b,params object[] args) => new(0,0,a,b); }
static class GUI { public static bool Button(Rect r,string s,GUIStyle style) => GUILayout.NextHit(); }
class TextField
{
    public string Text=""; public bool Editing; public static bool Commit;
    public bool Draw(Rect r,GUIStyle style,string placeholder,out bool canceled) { canceled=false; if (Gui.IsDim) { Editing=false; return false; } if (Commit) Editing=false; return Commit; }
}
namespace PatchShared { static class SpectrePatchHub { public static int ApplyFailureCount; } }
namespace SpectreReplay.PatchShared { static class SpectrePatchHub { public static int ApplyFailureCount { get => global::PatchShared.SpectrePatchHub.ApplyFailureCount; set => global::PatchShared.SpectrePatchHub.ApplyFailureCount=value; } } }
namespace Pages
{
    static class Pages
    {
        internal static int _remapRow = -1;
        static bool _remapFrom, _pickSaveDirPending, _pendingCommit; static int _saveDirPickVersion;
        static TextField _presetName = new();
        __PAGE_METHODS__
    }
}
""";

    private const string LayoutFixture = """
using System;
public class Fixture
{
    const float HeaderH=40, TabH=30;
    float _subPageT; int _lastSub; Vector2 _bodyScroll;
    __METHODS__
    float DrawBody(Rect area) { __PREFIX__ return top; }
    void DrawPatchWarningIfAny(int failures) { if (failures > 0) GUILayout.Used+=32; }
    public static bool[] Run()
    {
        bool ok=true; var f=new Fixture();
        foreach(int failures in new[] {0,3})
        {
            OnGui.PatchFailuresPublic=failures; GUILayout.Used=0; GUILayout.Tabs=0;
            float top=f.DrawBody(new Rect(0,0,900,700));
            ok &= GUILayout.AreaHeight >= GUILayout.Used && GUILayout.Used == 34+(failures>0?32:0) && top == 40+30+4+GUILayout.Used+8 && GUILayout.Tabs==1;
        }
        return new[] {ok};
    }
}
struct Rect { public float width,height; public Rect(float x,float y,float w,float h) { width=w;height=h; } }
struct Vector2 { public static Vector2 zero; }
static class OnGui { public static int CurrentTab,CurrentSubTab,PatchFailuresPublic; public static string[] SubTabLabelsFor(int page)=>new[]{"tab"}; public static void SetSubTab(int sub) { CurrentSubTab=sub; } }
static class Gui { public static float SegmentTabsH=34; public static int SegmentTabs(string[] options,int current,string key) { GUILayout.Used+=34; GUILayout.Tabs++; return current; } }
static class GUILayout { public static float AreaHeight,Used; public static int Tabs; public static void BeginArea(Rect area) { AreaHeight=area.height; } public static void EndArea() { } }
""";

    private const string PlaybackFixture = """
using System;
using System.IO;
public static class Fixture
{
    static string _pendingDelete, _loadedPath, _loadedCloudName; static bool PlayMode=true,is_playing;
    __METHODS__
    static void ApplyLoadedReplay(ReplayData data) { }
    static string CloudErrorText(string error) => error;
    public static bool[] Run()
    {
        bool ok=true; Settings.ReplayOn=false;
        LoadReplayFile("local.psprp"); ok &= Status.Keys.Contains("status.loaded_disabled") && !Status.Keys.Contains("status.pending_replay") && !Settings.ReplayOn;
        foreach(bool lost in new[]{false,true}) { Status.Keys=""; CloudReplay.Lost=lost; LoadReplayFromCloud("cloud.psprp"); ok &= Status.Keys.Contains("status.loaded_disabled") && !Status.Keys.Contains("status.pending_replay") && !Settings.ReplayOn; }
        Settings.ReplayOn=true; Status.Keys=""; LoadReplayFile("on.psprp"); ok &= Status.Keys.Contains("status.pending_replay");
        return new[]{ok};
    }
}
static class Settings { public static bool ReplayOn; }
class ReplayData { }
namespace Features.Replay { static class ReplayManager { public static bool LoadReplayAuto(out ReplayData data,string path) { data=new(); return true; } } }
static class CloudReplay { public static bool Lost; public static bool LoadFromCloud(out ReplayData data,string path,out string error,out bool lost) { data=new();error="";lost=Lost;return true; } }
static class Status { public static string Keys=""; public static void Say(string key,string detail=null) { Keys+=key+";"; } public static void Warn(string key,string detail=null) { Keys+=key+";"; } }
static class Debug { public static void LogWarning(string text) { } }
""";

    private const string CloudFixture = """
using System;
public static class Fixture
{
    __METHOD__
    static string CloudErrorText(string error) => "error:"+error;
    public static bool[] Run()
    {
        bool ok=true;
        foreach(bool restore in new[]{false,true})
        {
            Status.Detail=""; Status.Warning=false; QueueCloudAction(restore);
            ok &= Status.Warning && Status.Detail.Contains("2") && Status.Detail.Contains("3") && Status.Detail.Contains("error:offline");
        }
        return new[]{ok};
    }
}
struct Result { public int Restored,Uploaded,Failed; public string LastError; }
static class CloudReplay { static Result R()=>new(){Restored=2,Uploaded=2,Failed=3,LastError="offline"}; public static Result RestoreMissing()=>R(); public static Result UploadAllLocal()=>R(); }
static class MainThread { public static void Defer(Action action)=>action(); }
static class Loc { public static string F(string key,params object[] args) => string.Join("/",args); }
static class Status { public static string Detail; public static bool Warning; public static void Say(string key,string detail=null) { Detail=detail??"";Warning=false; } public static void Warn(string key,string detail=null) { Detail=detail??"";Warning=true; } }
static class Debug { public static void LogWarning(string text) { } }
""";
}
