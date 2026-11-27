using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json.Linq;

namespace UiPreview;

// 执行当前产品方法；仅替换引擎边界，不加载游戏或启动编码器。
internal static class UiToolRepairCheck
{
    internal static int Run()
    {
        int failed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine("  " + (ok ? "✓ " : "✗ ") + name);
            if (!ok) failed++;
        }
        void Execute(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) { Check(false, name + ": " + (ex.InnerException ?? ex).Message); }
        }
        Execute(() =>
        {
            // 数值提交已收口到共享 helper；执行它及真实草稿状态，不能用桩替代提交逻辑。
            var onGui = CSharpSyntaxTree.ParseText(Read("Ui/OnGui.cs")).GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "OnGui");
            string[] names = { "ModernSlider", "DrawSliderField", "RegisterSliderField" };
            var members = onGui.Members.Where(m => m is MethodDeclarationSyntax method && names.Contains(method.Identifier.ValueText)
                || m is ClassDeclarationSyntax c && c.Identifier.ValueText == "SliderDraft"
                || m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => v.Identifier.ValueText is "_sliderDrafts" or "_fieldCommitters"));
            var type = Compile(SliderFixture.Replace("__METHOD__", string.Join("\n", members), StringComparison.Ordinal));
            var result = (bool[])type.GetMethod("Run").Invoke(null, null);
            Check(result[0], "UT01 数值提交只应用输入值且布局组全部闭合");
            Check(result[1], "UT02 数值回调异常仍闭合布局组");
        }, "UT01/02 控件真源码执行");
        Execute(() =>
        {
            var tree = CSharpSyntaxTree.ParseText(Read("Ui/RoundedTex.cs")).GetRoot();
            var rounded = tree.DescendantNodes().OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == "RoundedTex");
            string[] names = { "To8", "Pack", "NewTex", "RoundSdf", "Well3D" };
            var members = rounded.Members.Where(m => m is StructDeclarationSyntax s && s.Identifier.ValueText == "Key"
                || m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => v.Identifier.ValueText == "WellCache")
                || m is MethodDeclarationSyntax method && names.Contains(method.Identifier.ValueText));
            var type = Compile(TextureFixture.Replace("__MEMBERS__", string.Join("\n", members), StringComparison.Ordinal));
            Check((bool)type.GetMethod("Run").Invoke(null, null), "UT03 相同槽纹理调用100次复用，颜色/半径独立缓存");
        }, "UT03 纹理真源码执行");
        Execute(() =>
        {
            string path = "Features/EffectRemover/EffectPresets.cs";
            string methods = string.Join("\n", new[] { "Save", "Apply", "SanitizeName", "IsSafeFileName" }.Select(n => Method(path,n).ToString()));
            var fields = Regex.Matches(methods, @"Settings\.([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value)
                .Append("EffectRemoverSetCameraZoom").Distinct().Select(n => "public static "
                    + (n == "EffectRemoverCameraZoomScale" ? "float " : "bool ") + n + ";");
            var type = Compile(PresetFixture.Replace("__METHODS__", methods, StringComparison.Ordinal)
                .Replace("__FIELDS__", string.Join("\n", fields), StringComparison.Ordinal)
                .Replace("__PRESET_FILE__", CSharpSyntaxTree.ParseText(Read("Features/EffectRemover/PresetFile.cs"))
                    .GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Single(t => t.Identifier.ValueText == "PresetFile").ToString(), StringComparison.Ordinal));
            Check((bool)type.GetMethod("Run").Invoke(null, null), "UT04 相机缩放开关随预设双向还原，旧预设保留现值");
        }, "UT04 预设真源码执行");
        Execute(() =>
        {
            var handle = Method("Ui/Timeline/TimelineOverlay.cs", "HandleInput");
            var prefix = handle.Body.Statements.TakeWhile(s => s is not LocalDeclarationStatementSyntax local
                || !local.Declaration.Variables.Any(v => v.Identifier.ValueText == "mouse"));
            var type = Compile(TimelineFixture.Replace("__GUARD__", string.Join("\n", prefix), StringComparison.Ordinal));
            Check((bool)type.GetMethod("Run").Invoke(null, null), "UT05 设置窗打开时不轮询时间轴鼠标且清掉旧拖拽，关窗恢复");
        }, "UT05 时间轴输入接线执行");
        Execute(() =>
        {
            string[] names = { "GetMain_Postfix", "GetState_Postfix", "GetStateKeys_PostfixOld", "GetStateKeys_Postfix", "IsGameplayAction" };
            string consumers = string.Join("\n", names.Select(n => Method("Features/Replay/Patches/Patch_InputInjection.cs", n).WithAttributeLists(default).ToString()));
            var type = Compile(TimelineUpdateFixture.Replace("__UPDATE__", Method("Ui/Timeline/TimelineOverlay.cs", "Update").ToString(), StringComparison.Ordinal)
                .Replace("__CONSUMERS__", consumers, StringComparison.Ordinal));
            var results = (bool[])type.GetMethod("Run").Invoke(null, null);
            Check(results[0], "UT09 实际Update隐藏路径清拖拽/悬停/焦点状态，再显示不恢复旧拖拽");
            Check(results[1], "UT10 三个时间轴输入框聚焦时捕获输入且停止旧拖拽，失焦恢复");
            Check(results[2], "UT11 焦点捕获经真实RDInput补丁阻断托管/原生玩法输入，保留界面动作");
        }, "UT09 时间轴隐藏路径执行");
        const string charSource = """
char quote = '"'; // remove-one
char slash = '\\'; /* remove-two */ int value = 1;
""";
        string clean = SrcUtil.Clean(charSource);
        Check(!clean.Contains("remove-one", StringComparison.Ordinal) && !clean.Contains("remove-two", StringComparison.Ordinal)
            && clean.Contains("int value = 1;", StringComparison.Ordinal) && clean.Contains("'\"'", StringComparison.Ordinal),
            "UT06 双引号/反斜线字符不破坏后续注释剥离");
        const string stringSource = """
string text = "the game's // text"; // remove-three
char escaped = '\''; // remove-four
int value = 2;
""";
        string stringClean = SrcUtil.Clean(stringSource);
        Check(stringClean.Contains("the game's // text", StringComparison.Ordinal)
            && !stringClean.Contains("remove-three", StringComparison.Ordinal)
            && !stringClean.Contains("remove-four", StringComparison.Ordinal) && stringClean.Contains("int value = 2;", StringComparison.Ordinal),
            "UT07 字符串撇号、转义单引号保持注释词法边界");
        Execute(() =>
        {
            string tools = Path.Combine(Driver.ProjectDir, "..", "tools");
            string script = File.ReadAllText(Path.Combine(tools, "mutate-regression.ps1"));
            string source = File.ReadAllText(Path.Combine(tools, "mutate.ps1"));
            var match = Regex.Match(script, """Matches\(\$text, "([^"]+)"\)""");
            string[] actual = Regex.Matches(source, @"'(MUT-[A-Za-z0-9-]+)'\s*\{").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
            string[] registered = match.Success ? Regex.Matches(source, match.Groups[1].Value).Select(m => m.Groups[1].Value).Distinct().Order().ToArray() : Array.Empty<string>();
            Check(actual.Length > 400 && actual.SequenceEqual(registered), "UT08 全量变异注册涵盖所有真实标签（含AV1/HDR10连字符）");
        }, "UT08 注册表执行");
        return failed;
    }
    private static string Read(string path) => SrcUtil.Read(Driver.ProjectDir, path);
    private static MethodDeclarationSyntax Method(string path, string name)
        => CSharpSyntaxTree.ParseText(Read(path)).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == name);
    private static Type Compile(string code)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
            .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
            .Append(typeof(JObject).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("UiToolFixture_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(code) }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        if (!result.Success) throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(6)));
        return Assembly.Load(image.ToArray()).GetType("Fixture");
    }
    private const string SliderFixture = """
using System;
using System.Collections.Generic;
using System.Globalization;
public static class Fixture
{
    static readonly TextField field = new TextField();
    static TextField GetField(string key) => field;
    static void SyncField(TextField value, string text) { }
    static string NumberOf(string text) => text;
    static void DrawModernTextStack(string key) { }
    static bool TryParse(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    __METHOD__
    public static bool[] Run()
    {
        field.Text = "30";
        var values = new List<float>();
        ModernSlider("test", 10, 0, 100, n => n.ToString(CultureInfo.InvariantCulture), values.Add, true);
        bool normal = Gui.Depth == 0 && values.Count == 1 && values[0] == 30;
        Gui.Depth = 0;
        bool callbackThrew = false;
        try { ModernSlider("test", 10, 0, 100, n => n.ToString(), n => throw new Exception("callback"), true); }
        catch (Exception ex) when (ex.Message == "callback") { callbackThrew = true; }
        return new[] {normal, callbackThrew && Gui.Depth == 0};
    }
}
class TextField { public string Text; public bool Editing; public bool Draw(Rect r, object style, string label, out bool canceled) { canceled = false; return true; } }
class Rect { }
static class Gui
{
    public static int Depth;
    public static bool IsDim;
    public static object ModernDescStyle, LabelStyle;
    public static float ModernLabelW, ModernValueW;
    public static void Dim(bool dim, Action draw) => draw();
    public static void BeginModernRow() => Depth++;
    public static void EndModernRow() => Depth--;
    public static float Slider(Rect r, float value, float min, float max) => 20;
}
static class GUILayout
{
    public static void Space(float n) { }
    public static void BeginHorizontal() => Gui.Depth++;
    public static void EndHorizontal() => Gui.Depth--;
    public static void Label(string text, object style, params object[] options) { }
    public static object Width(float v) => null;
    public static object Height(float v) => null;
    public static object ExpandWidth(bool v) => null;
}
static class GUILayoutUtility { public static Rect GetRect(float w, float h, params object[] options) => new Rect(); }
static class Loc { public static string T(string key) => key; }
static class Mathf
{
    public static bool Approximately(float a, float b) => Math.Abs(a-b) < 0.001;
    public static float Clamp(float value, float min, float max) => Math.Clamp(value,min,max);
}
""";
    private const string TextureFixture = """
using System;
using System.Collections.Generic;
public static class Fixture
{
    __MEMBERS__
    public static bool Run()
    {
        Color fill = new Color(0.2f, 0.3f, 0.4f, 1f);
        var first = Well3D(fill, 4);
        bool same = true;
        for (int i=0;i<100;i++) same &= ReferenceEquals(first, Well3D(fill,4));
        return same && !ReferenceEquals(first, Well3D(Color.black,4)) && !ReferenceEquals(first,Well3D(fill,5)) && Texture2D.Created == 3;
    }
}
class Texture2D
{
    public static int Created;
    public Texture2D(int w,int h,TextureFormat f,bool mip) { Created++; }
    public FilterMode filterMode;
    public TextureWrapMode wrapMode;
    public HideFlags hideFlags;
    public void SetPixels(Color[] pixels) { }
    public void Apply(bool mip,bool unreadable) { }
}
enum TextureFormat { RGBA32 }
enum FilterMode { Bilinear }
enum TextureWrapMode { Clamp }
enum HideFlags { HideAndDontSave }
struct Color
{
    public float r,g,b,a;
    public Color(float r,float g,float b,float a=1) { this.r=r;this.g=g;this.b=b;this.a=a; }
    public static Color black => new Color(0,0,0);
    public static Color white => new Color(1,1,1);
    public static Color Lerp(Color x,Color y,float t) => new Color(x.r+(y.r-x.r)*t,x.g+(y.g-x.g)*t,x.b+(y.b-x.b)*t,x.a+(y.a-x.a)*t);
}
static class Mathf
{
    public static int Clamp(int v,int a,int b) => Math.Clamp(v,a,b);
    public static int RoundToInt(float v) => (int)MathF.Round(v);
    public static float Clamp01(float v) => Math.Clamp(v,0,1);
}
""";
    private const string PresetFixture = """
using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
public static class Fixture
{
    private static string PresetFolder;
    __METHODS__
    public static bool Run()
    {
        PresetFolder = Path.Combine(Path.GetTempPath(), "srm-preset-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Settings.EffectRemoverSetCameraZoom = true;
            Settings.EffectRemoverCameraZoomScale = 150;
            var saved = Save("zoom");
            Settings.EffectRemoverSetCameraZoom = false;
            Settings.EffectRemoverCameraZoomScale = 300;
            var restored = Apply(saved.FileName);
            bool result = saved.Ok && restored.Ok && Settings.EffectRemoverSetCameraZoom && Settings.EffectRemoverCameraZoomScale == 150;
            File.WriteAllText(Path.Combine(PresetFolder,"old.json"), "{\"settings\":{\"EffectRemoverCameraZoomScale\":200}}");
            Apply("old.json");
            return result && Settings.EffectRemoverSetCameraZoom && Settings.EffectRemoverCameraZoomScale == 200;
        }
        finally { if (Directory.Exists(PresetFolder)) Directory.Delete(PresetFolder,true); }
    }
}
static class Settings { __FIELDS__ }
__PRESET_FILE__
static class Mathf { public static float Clamp(float v,float min,float max) => Math.Clamp(v,min,max); }
""";
    private const string TimelineUpdateFixture = """
using System;
using System.Collections.Generic;
public class Fixture
{
    bool _inputCaptured, _dragResize, _dragPanel, _overTrack, _overTitleBar, _floorFieldFocused, polled, drawn;
    int _dragMode;
    static bool InLevelScene = true;
    Canvas _canvas = new Canvas();
    InputField _floorSeekField, _selStartField, _selEndField;
    void HandleInput(ReplayTimelineState state) { polled = true; _inputCaptured = false; TimelineController.SetInputCaptured(false); }
    void UpdateVisuals(ReplayTimelineState state) { drawn = true; }
    __UPDATE__
    __CONSUMERS__
    public static bool[] Run()
    {
        bool result = true;
        for (int reason = 0; reason < 8; reason++)
        {
            var f = new Fixture();
            f._inputCaptured = f._dragResize = f._dragPanel = f._overTrack = f._overTitleBar = f._floorFieldFocused = true;
            f._dragMode = 2;
            f._canvas.enabled = TimelineController.Captured = true;
            ModernSettingsWindow.IsOpen = reason == 0;
            ModernSettingsWindow.IsClosing = reason == 1;
            Settings.TimelineOn = reason != 2;
            TimelineController.State = new ReplayTimelineState { Source = reason == 3 ? null : new object(), Duration = reason == 4 ? 0 : 10 };
            SpectreState.PlayMode = reason != 5;
            InLevelScene = reason != 6;
            SpectreReplay.Features.VideoRender.VideoRender.Active = reason == 7;
            f.Update();
            result &= !f.polled && !f.drawn && !f._canvas.enabled && !f._inputCaptured
                && !f._dragResize && !f._dragPanel && f._dragMode == 0
                && !f._overTrack && !f._overTitleBar && !f._floorFieldFocused && !TimelineController.Captured;
            ModernSettingsWindow.IsOpen = ModernSettingsWindow.IsClosing = false;
            Settings.TimelineOn = SpectreState.PlayMode = InLevelScene = true;
            TimelineController.State.Source = new object();
            TimelineController.State.Duration = 10;
            SpectreReplay.Features.VideoRender.VideoRender.Active = false;
            f.Update();
            result &= f.polled && f.drawn && f._canvas.enabled && !f._dragResize && !f._dragPanel && f._dragMode == 0;
        }
        bool focused = true;
        for (int field = 0; field < 3; field++)
        {
            var f = new Fixture { _dragResize = true, _dragPanel = true, _dragMode = 2 };
            var input = new InputField { isFocused = true };
            if (field == 0) f._floorSeekField = input;
            if (field == 1) f._selStartField = input;
            if (field == 2) f._selEndField = input;
            f.Update();
            focused &= !f.polled && f.drawn && f._inputCaptured && TimelineController.Captured
                && !f._dragResize && !f._dragPanel && f._dragMode == 0;
            input.isFocused = false;
            f.Update();
            focused &= f.polled && !f._inputCaptured && !TimelineController.Captured;
        }
        bool blocked = true;
        foreach (bool native in new[] { false, true })
        {
            ManagedInput.Active = true; ManagedInput.NativeTakeover = native;
            TimelineController.Captured = true;
            int count = 7; GetMain_Postfix(ref count, ButtonState.IsDown);
            bool action = true; GetState_Postfix(ref action, InputAction.Confirm, ButtonState.IsDown);
            bool cancel = true; GetState_Postfix(ref cancel, InputAction.Cancel, ButtonState.IsDown);
            var oldKeys = new System.Collections.Generic.List<AnyKeyCode> { new AnyKeyCode() };
            GetStateKeys_PostfixOld(ref oldKeys, ButtonState.IsDown);
            var keys = new System.Collections.Generic.List<AnyKeyCode> { new AnyKeyCode() };
            GetStateKeys_Postfix(keys, ButtonState.IsDown);
            blocked &= count == 0 && !action && cancel && oldKeys.Count == 0 && keys.Count == 0;
            TimelineController.Captured = false;
            count = 7; GetMain_Postfix(ref count, ButtonState.IsDown);
            blocked &= count == (native ? 7 : 3);
        }
        ManagedInput.Active = false; TimelineController.Captured = true;
        int inactive = 7; GetMain_Postfix(ref inactive, ButtonState.IsDown);
        blocked &= inactive == 7;
        return new[] { result, focused, blocked };
    }
}
class Canvas { public bool enabled; }
class InputField { public bool isFocused; }
class ReplayTimelineState { public object Source; public double Duration; }
static class TimelineController
{
    public static ReplayTimelineState State;
    public static bool Captured;
    public static bool InputCaptured => Captured;
    public static void SetInputCaptured(bool value) => Captured = value;
    public static void Tick(double value) { }
}
static class ModernSettingsWindow { public static bool IsOpen, IsClosing; }
static class Settings { public static bool TimelineOn; }
static class SpectreState { public static bool PlayMode; }
static class ReplayPlayer { public static bool TryGetSongPosition(out double value) { value = 1; return true; } }
class AnyKeyCode { }
enum ButtonState { IsDown }
enum InputAction { Action1, Action2, Confirm, Left, Right, Up, Down, LeftAlt, RightAlt, FaceUp, FaceLeft, Cancel }
static class ManagedInput
{
    public static bool Active = true, NativeTakeover;
    public static int Count(ButtonState state) => 3;
    public static bool Matches(InputAction action, ButtonState state) => true;
    public static void Append(System.Collections.Generic.List<AnyKeyCode> keys, ButtonState state) => keys.Add(new AnyKeyCode());
}
namespace SpectreReplay.Features.VideoRender { static class VideoRender { public static bool Active; } }
""";
    private const string TimelineFixture = """
using System;
public static class Fixture
{
    static bool _inputCaptured, _dragResize, _dragPanel, _overTrack, _overTitleBar, _floorFieldFocused, polled;
    static int _dragMode;
    static void HandleInput() { __GUARD__ polled = true; }
    public static bool Run()
    {
        _inputCaptured=_dragResize=_dragPanel=_overTrack=_overTitleBar=_floorFieldFocused=true;
        _dragMode=2;
        ModernSettingsWindow.IsOpen=true;
        TimelineController.Captured=true;
        HandleInput();
        bool blocked=!polled && !_inputCaptured && !_dragResize && !_dragPanel && _dragMode==0 && !TimelineController.Captured;
        ModernSettingsWindow.IsOpen=false;
        HandleInput();
        return blocked && polled;
    }
}
static class ModernSettingsWindow { public static bool IsOpen, IsClosing; }
static class TimelineController { public static bool Captured; public static void SetInputCaptured(bool value) => Captured=value; }
""";
}
