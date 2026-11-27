using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UiPreview;

// 真源码分支 + 纯容器：不启动游戏、不碰 user32/麦克风。Run 返回实际失败数。
internal static class ShippingReplayCheck
{
    private static string root;
    private static int failures;
    internal static int Run() => Run(Driver.ProjectDir);
    internal static int Run(string projectDir)
    {
        root = projectDir;
        failures = 0;
        Test("F10/F13", ControlFixture());
        Test("F11", InputFixture());
        Test("F15", JudgmentFixture());
        Test("F27", SampleFixture());
        Test("F30", ReplayLoadFixture());
        SaveFixture();
        Test("F21 pure crypto", CryptoFixture, Read("Features/Replay/Codec/ReplayCrypto.cs"),
            Read("Features/Replay/Runtime/ReplaySaveOperation.cs"));
        return failures;
    }

    private static string Read(string path)
    {
        string file = Path.Combine(root, path);
        if (!File.Exists(file)) file = Path.Combine(Directory.GetCurrentDirectory(), "SpectreReplay", path);
        return File.ReadAllText(file, Encoding.UTF8);
    }
    private static ClassDeclarationSyntax Type(string path, string name)
        => CSharpSyntaxTree.ParseText(Read(path)).GetRoot().DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(t => t.Identifier.ValueText == name);
    private static string Method(ClassDeclarationSyntax type, string name)
        => type.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name).ToString();
    private static void Test(string name, string code, params string[] extra)
    {
        try
        {
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var trees = new[] { code }.Concat(extra).Select(s => CSharpSyntaxTree.ParseText(s));
            var c = CSharpCompilation.Create("ShippingReplay_" + Guid.NewGuid().ToString("N"), trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            var result = c.Emit(bytes);
            if (!result.Success) throw new InvalidOperationException(string.Join("; ", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Take(12)));
            var fixture = Assembly.Load(bytes.ToArray()).GetType("Fixture");
            var lines = (string[])fixture.GetMethod("Run").Invoke(null, null);
            foreach (string line in lines)
            {
                Console.WriteLine("  " + line);
                if (line.StartsWith("✗", StringComparison.Ordinal)) failures++;
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("  ✗ " + name + " 真实源码夹具未执行: " + (ex.InnerException ?? ex).Message);
        }
    }
    private const string Checks = """
static readonly System.Collections.Generic.List<string> Lines = new();
static void Check(bool ok, string text) => Lines.Add((ok ? "✓ " : "✗ ") + text);
""";

    private const string CryptoFixture = """
using System;using System.Threading;using SpectreReplay.Features.Replay;
public static class Fixture {
 public static int Logs,Writes;
 public static string[] Run() {
  bool threw=false;var thread=new Thread(()=>{try{ReplayCrypto.EncryptCore(null);}catch(ArgumentNullException){threw=true;}});
  thread.Start();thread.Join();
  bool pure=threw&&Logs==0;Logs=0;
  var op=new ReplaySaveOperation(new ReplayData{FileName="invalid"},"unused","safe",false,false,false,null);
  op.Start();var until=DateTime.UtcNow.AddSeconds(5);while(!op.IsCompleted&&DateTime.UtcNow<until)Thread.Sleep(1);
  bool failed=op.IsCompleted&&!op.Success&&op.Error!=null&&Logs==0&&Writes==0;
  return new[]{(pure?"✓ ":"✗ ")+"F21C1 纯加密核心拒空并上抛，后台不调用Unity日志",
   (failed?"✓ ":"✗ ")+"F21C2 真实后台保存遇到空编码载荷时失败且不写盘、不调用Unity日志"};
 }
}
namespace SpectreReplay { static class AtomicFile { public static void WriteAllBytes(string p,byte[] b){Fixture.Writes++;} } }
namespace SpectreReplay.Features.Replay {
 class ReplayData { public string FileName,KeybdSoundFileName,KeybdSoundHash,KeybdSoundSourcePath; public byte[] KeybdSoundMemory;public Stop KeybdSoundStop;public Blob KeybdAudio; }
 class Stop { public bool SaveQueued;public string AudioPath; } struct Blob {public byte[] Payload;public bool HasData=>Payload!=null;}
 class Replay {public DateTimeOffset EndTime;}static class ReplayDecoder{public static Replay FromReplayData(ReplayData d)=>new();}
 static class ReplayEncoder {public static byte[] Encode(Replay r)=>null;}
 static class ReplayConstants {public const string ExtensionNoEncrypt=".psprp",Extension=".sprp";}
 static class KeybdAudioCodec {public const int QualityExtreme=0,QualityBalanced=1;public static Blob Encode(byte[] b,int q,out string error){error=null;return default;}}
}
namespace UnityEngine { static class Debug {public static void Log(object o){Interlocked.Increment(ref Fixture.Logs);} } }
""";

    private static string ControlFixture()
    {
        var api = Type("State/SpectreAPI.cs", "SpectreAPI");
        var player = Type("Features/Replay/Runtime/ReplayPlayer.cs", "ReplayPlayer");
        string block = api.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "BlockPlaying"
            && m.ParameterList.Parameters.Count == 2).ToString();
        return """
using System; using System.Collections.Generic;
using static SpectreState;
public static class Fixture {
__CHECKS__
public static List<string> Events = new();
static object _blockLock = new(); static Dictionary<string,string> _blockSources = new();
__BLOCK__
public static string[] Run() {
 Features.VideoRender.VideoRender.CaptureInProgress=true; is_playing=true;
 BlockPlaying("fixture","stop");
 Check(Events.Count==2 && Events[0]=="cancel" && Events[1]=="stop" && !Features.VideoRender.VideoRender.Completed,
  "F10 BlockPlaying 先取消捕获再停止，不产生截断Completed");
 Events.Clear(); SpectreAPI.Stopped=0; ApiBlockPlaying=false; is_playing=true; data=new Data(); data.KeyEvents.Add(new Key()); KeyEventIndex=1;
 ReplayPlayer.PlayKeyboardEvent(true); ReplayPlayer.PlayKeyboardEvent(true);
 bool early=SpectreAPI.Stopped==1 && !is_playing;
 is_playing=true; KeyEventIndex=0; ReplayPlayer.PlayKeyboardEvent(true); ReplayPlayer.PlayKeyboardEvent(true);
 Check(early && SpectreAPI.Stopped==2 && !is_playing,"F13 Won耗尽前/泵后两分支恰好一次PlaybackStopped");
 return Lines.ToArray(); }
}
static class MainThread { public static void Run(Action a)=>a(); }
static class SpectreState {
 public static bool is_playing,ApiBlockPlaying; public static int PlayIndex,KeyEventIndex;
 public static Data data;
 public static void TriggerMessage(string s) { }
 public static class PlayActions { __STOP_PLAYING__ }
}
static class SpectreAPI { public static int Stopped; public static void RaisePlaybackStopped()=>Stopped++; }
static class ReplayPlayer {
 public static void Stop(){if(!is_playing)return;Fixture.Events.Add("stop");is_playing=false;
  if(Features.VideoRender.VideoRender.CaptureInProgress)Features.VideoRender.VideoRender.Completed=true;}
 static double CurrentSongPosition(scrConductor c,out bool ready){ready=true;return 10;}
 static void PumpInput(double p,scrConductor c){KeyEventIndex=data.KeyEvents.Count;}
 __PLAY_KEYS__
}
static class LocalizationManager { public static string GetLocalizedText(string s)=>s; }
static class Debug { public static void Log(string s){} public static void LogException(Exception e){} }
class Data { public List<Key> KeyEvents=new(); } class Key { }
class scrConductor {} static class ADOBase { public static scrConductor conductor=new(); }
namespace Features.VideoRender { static class VideoRender {
 public static bool CaptureInProgress,Completed;
 public static void CancelRender(){Fixture.Events.Add("cancel");CaptureInProgress=false;}
} }
""".Replace("__CHECKS__", Checks).Replace("__BLOCK__", block)
            .Replace("__STOP_PLAYING__", Method(Type("State/SpectreState.cs", "PlayActions"), "StopPlaying"))
            .Replace("__PLAY_KEYS__", Method(player, "PlayKeyboardEvent"));
    }

    private static string ReplayLoadFixture()
    {
        var manager = Type("Features/Replay/Runtime/ReplayManager.cs", "ReplayManager");
        var render = Type("Features/VideoRender/Pipeline/VideoRender.cs", "VideoRender");
        var job = Type("Features/VideoRender/Pipeline/VideoRenderJob.cs", "VideoRenderJob");
        return """
using System; using System.Collections.Generic; using System.Linq;
using SpectreReplay; using SpectreReplay.Features.Replay;
using SpectreReplay.Features.VideoRender; using UnityEngine;
public static class Fixture {
__CHECKS__
public static readonly List<string> Events = new();
public static bool Valid, Playing;
public static string[] Run() {
 foreach (bool memory in new[] { false, true }) {
  foreach (bool valid in new[] { true, false }) {
   foreach (bool capturing in new[] { true, false }) {
    Valid = valid; Playing = true; Events.Clear();
    VideoRender.CaptureInProgress = capturing; VideoRender.Job = new VideoRenderJob();
    bool loaded = memory
      ? ReplayManager.LoadReplayFromMemory(out _, new byte[] { 1 }, "next.sprp")
      : ReplayManager.LoadReplay(out _, "next.sprp");
    string[] expected = !valid ? new[] { "decode" }
      : new[] { "decode", "play-stop", "record-stop", "audio-clear", "audio-load", "timeline-load" };
    bool correct = loaded == valid && Events.SequenceEqual(expected)
      && VideoRender.Job.CancelRequested == (valid && capturing)
      && Playing == !valid;
    Check(correct, "F30 " + (memory ? "内存" : "本地")
      + (valid ? capturing ? "加载成功先取消捕获再停旧会话" : "加载成功不取消非捕获作业"
               : capturing ? "解码失败保留捕获与旧会话" : "解码失败保留旧会话"));
   }
  }
 }
 return Lines.ToArray();
}
}
namespace SpectreReplay.Features.Replay {
 public class ReplayData { }
 static class ReplayManager {
 __LOAD_FILE__
 __LOAD_MEMORY__
 static bool ReadReplayData(out ReplayData data, string path, bool plain) {
  Fixture.Events.Add("decode"); data = new(); return Fixture.Valid;
 }
 static bool ReadReplayData(out ReplayData data, byte[] bytes, string name, bool plain) {
  Fixture.Events.Add("decode"); data = new(); return Fixture.Valid;
 }
 static void TryLoadKeybdSound(string path, ReplayData data) => Fixture.Events.Add("audio-load");
 static void TryLoadKeybdSoundFromMemory(ReplayData data, byte[] wav) => Fixture.Events.Add("audio-load");
 }
}
namespace SpectreReplay {
 static class SpectreState { public static class PlayActions {
  public static void StopPlaying() {
   // 实际 CancelRender/RequestCancel 只置标志，不会立刻清 CaptureInProgress。
   // 在停止旧会话的这一拍就检查标志，防止「先停再取消」也蒙混通过。
   if (VideoRender.CaptureInProgress && !VideoRender.Job.CancelRequested)
    Fixture.Events.Add("premature-stop");
   Fixture.Events.Add("play-stop"); Fixture.Playing = false;
  }
  public static void StopRecording() => Fixture.Events.Add("record-stop");
 } }
 static class WavLoader { public static void ClearLoadedClip() => Fixture.Events.Add("audio-clear"); }
 static class TimelineController { public static void Load(ReplayData data, object unused) => Fixture.Events.Add("timeline-load"); }
}
namespace SpectreReplay.Features.VideoRender {
 static class VideoRender {
  public static bool CaptureInProgress; public static VideoRenderJob Job;
  __CANCEL_RENDER__
 }
 sealed class VideoRenderJob {
  private volatile bool cancelRequested;
  internal bool CancelRequested => cancelRequested;
  __REQUEST_CANCEL__
 }
}
namespace UnityEngine { static class Debug { public static void Log(string text) { } } }
""".Replace("__CHECKS__", Checks)
            .Replace("__LOAD_FILE__", Method(manager, "LoadReplay"))
            .Replace("__LOAD_MEMORY__", Method(manager, "LoadReplayFromMemory"))
            .Replace("__CANCEL_RENDER__", Method(render, "CancelRender"))
            .Replace("__REQUEST_CANCEL__", Method(job, "RequestCancel"));
    }

    private static string InputFixture()
    {
        var managed = Type("Features/Replay/Runtime/ManagedInput.cs", "ManagedInput");
        string[] fields = { "Held", "SourceHeld", "SourceVk", "HeldSourceCount", "PressedFrame", "PressCountFrame", "ReleasedFrame", "_heldCount", "_loggedFirstInjection", "_takeoverFrame", "_takeover" };
        string members = string.Join("\n", managed.Members.Where(m => m is FieldDeclarationSyntax f
            && f.Declaration.Variables.Any(v => fields.Contains(v.Identifier.ValueText))))
            + "\n" + string.Join("\n", new[] { "Note", "TrackSource", "Clear", "SnapshotHeld", "IsHeldVk" }.Select(n => Method(managed,n)));
        var pump = Type("Features/Replay/Runtime/ReplayPlayer.cs", "ReplayPlayer").Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText=="PumpInput" && m.ParameterList.Parameters.Count==2);
        string loop=pump.DescendantNodes().OfType<WhileStatementSyntax>().Single().ToString();
        return """
using System;using System.Collections.Generic;using UnityEngine;
public static class Fixture {
__CHECKS__
public static string[] Run(){
 foreach(ushort mapped in new ushort[]{0,27,70}){
  ManagedInput.Clear(); CurrKeyState=new byte[256]; Settings.Hotkey=0;
  Feed(true,65,65,true); Settings.Hotkey=70; Feed(false,65,mapped,false);
  Check(!ManagedInput.IsHeldVk(65) && CurrKeyState[65]==0,
   "F11 remap UP→"+mapped+"释放旧托管/OS目标(镜像已关/新热键仍释放)");
 }
 ManagedInput.Clear();CurrKeyState=new byte[256];Settings.Hotkey=0;
 Feed(true,65,65,true);Settings.Hotkey=65;Feed(false,65,65,true);
 Check(!ManagedInput.IsHeldVk(65)&&CurrKeyState[65]==0,"F11 已镜像目标中途成为热键仍补发OS松键");
 ManagedInput.Clear();CurrKeyState=new byte[256];Settings.Hotkey=0;
 Feed(true,65,67,true);Feed(true,66,67,true);Feed(false,65,0,false);
 bool held=ManagedInput.IsHeldVk(67);Feed(false,66,27,false);
 Check(held&&!ManagedInput.IsHeldVk(67)&&CurrKeyState[67]==0,"F11 多来源聚合最后一个UP才释放旧目标");
 return Lines.ToArray();}
static Data data=new();static int KeyEventIndex;static bool _mirrorUsedThisPlayback;
static byte[] CurrKeyState=new byte[256];
static bool IsBlockedKey(ushort k)=>k==16||k==17||k==18||k==27;
static bool IsExtendedKey(ushort k)=>false;
static void Feed(bool down,ushort source,ushort map,bool sendInput){
 Settings.key_code_convert[source]=map;data.KeyEvents=new(){new Key{KeyCode=source,IsPressed=down}};KeyEventIndex=0;
 var inputs=new List<KeyboardSimulation.INPUT>();bool asyncInject=false;double songPosition=1;int pumped=0;const int MaxEventsPerFrame=64;
 __LOOP__
}
}
static class ManagedInput { __MEMBERS__ public static bool IsModHotkey(ushort k)=>k==Settings.Hotkey; }
class Key{public ushort KeyCode;public bool IsPressed;public double SongPosition;}
class Data{public List<Key> KeyEvents=new();}
static class Settings{public static ushort[] key_code_convert=new ushort[256];public static ushort Hotkey;}
static class KeyboardSimulation{public struct INPUT{} public static INPUT MakeInput(byte k,bool p,bool e)=>new();}
static class AsyncInputInjector{public static bool Healthy=true;public static void Inject(ushort k,bool p,double d){} }
static class QuartzLiveBridge{public static void Edge(ushort k,bool p){} }
static class ReplayInputBridge{public static void Edge(ushort k,bool p){} }
namespace Features.VideoRender{static class RenderOverlayCapture{public static void QuartzKey(ushort k,bool p){}}}
namespace API{static class SpectreAPI{public static void RaiseReplayKeyChanged(ushort k,bool p){}}}
namespace UnityEngine{static class Time{public static int frameCount=7;}static class Debug{public static void Log(string s){}public static void LogException(Exception e){}}}
""".Replace("__CHECKS__",Checks).Replace("__MEMBERS__",members).Replace("__LOOP__",loop);
    }

    private static string JudgmentFixture()
    {
        var type=Type("Features/Replay/Data/ReplayFileInfo.cs","ReplayFileInfo");
        string members=string.Join("\n",type.Members.Where(m=>m is FieldDeclarationSyntax f
            && f.Declaration.Variables.Any(v=>v.Identifier.ValueText=="JudgmentNames"||v.Identifier.ValueText=="JudgmentLocaleKeys")))
            + "\n"+string.Join("\n",new[]{"JudgmentRow","JudgmentRowVisual","JudgmentTone"}.Select(n=>Method(type,n)));
        return """
using System;using UnityEngine;
public class Fixture{
__CHECKS__
int[] JudgmentCounts=new[]{3,4,5};
__MEMBERS__
public static string[] Run(){
 foreach(bool modern in new[]{false,true}){
  SpectreReplay.Features.Compat.HitMarginCompat.HasXPerfect=modern;
  var f=new Fixture();int total=0,perfect=0;bool correctTone=true;
  for(int r=0;r<JudgmentNames.Length;r++)if(f.JudgmentRowVisual(r,out string key,out int n,out Color c)){
   total+=n;if(key=="playback.judge.perfect")perfect+=n;correctTone &= c.r==0.30f && c.g==0.78f && c.b==0.42f;
  }
  Check((modern?total==12&&perfect==0:total==3&&perfect==3)&&correctTone,
   "F15 "+(modern?"新枚举不重复旧Perfect别名":"旧Perfect完整计数/正确文案/绿色"));
 }
 return Lines.ToArray();}
}
namespace SpectreReplay.Features.Compat{static class HitMarginCompat{
 public static bool HasXPerfect;
 public static bool TryResolve(string n,out int v){v=0;if(n=="Perfect")return true;
  if(!HasXPerfect)return false;
  if(n=="PerfectMinus")return true;if(n=="XPerfect"){v=1;return true;}if(n=="PerfectPlus"){v=2;return true;}return false;}
}}
static class RDConstants{public static Constants data;}class Constants{public Scheme hitMarginColoursUI;}class Scheme{public UnityEngine.Color SelectByHitMargin(int i)=>new(0.3f,0.78f,0.42f);}
namespace UnityEngine{public struct Color{public float r,g,b;public Color(float r,float g,float b){this.r=r;this.g=g;this.b=b;}public static Color white=>new(1,1,1);}}
""".Replace("__CHECKS__",Checks).Replace("__MEMBERS__",members);
    }

    private static string SampleFixture()
    {
        return """
using System;using UnityEngine;
public static class Fixture{
__CHECKS__
static bool _dllMissing;static int GetKeyboardState(byte[] b)=>throw new DllNotFoundException();
__SAMPLE__
public static string[] Run(){var b=new byte[256];b[97]=128;SampleKeyState(b);
 Check(b[65]==128&&b[90]==128&&b[38]==128&&b[160]==128&&b[97]==0,
  "F27 Unity字母/方向键/Shift同源转换到VK，清理陈旧采样");return Lines.ToArray();}
}
static class KeyViewerBridge{
static int CachedNativeVk(KeyCode c)=>c switch{KeyCode.A=>65,KeyCode.Z=>90,KeyCode.UpArrow=>38,KeyCode.LeftShift=>160,_=>-1};
__TO_VK__
}
namespace UnityEngine{enum KeyCode{None=0,A=97,Z=122,UpArrow=273,LeftShift=304}static class Input{public static bool GetKey(KeyCode k)=>k!=KeyCode.None;}}
""".Replace("__CHECKS__",Checks)
            .Replace("__SAMPLE__",Method(Type("Features/Replay/Runtime/KeyboardSimulation.cs","KeyboardSimulation"),"SampleKeyState"))
            .Replace("__TO_VK__",Method(Type("Features/Replay/Runtime/KeyViewerBridge.cs","KeyViewerBridge"),"ToNativeVk"));
    }

    private static void SaveFixture()
    {
        // 实现见下方：独立编译真实数据、录音完成令牌、后台保存器和容器。
        RunSaveFixture();
    }

    private static void RunSaveFixture()
    {
        var state = Type("State/SpectreState.cs", "SpectreState");
        var manager = Type("Features/Replay/Runtime/ReplayManager.cs", "ReplayManager");
        var recorder = Type("State/AudioRecorder.cs", "AudioRecorder");
        string stateMembers = Method(state,"SaveFile");
        var complete = state.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(m=>m.Identifier.ValueText=="CompleteSave");
        if (complete != null) stateMembers += "\n" + complete;
        var observer = state.Members.OfType<ClassDeclarationSyntax>().SingleOrDefault(t=>t.Identifier.ValueText=="SaveCompletion");
        if (observer != null) stateMembers += "\n" + observer;
        string managerMembers=string.Join("\n",new[]{"EnsureDirectory","TryEmbedKeybdSound","SaveReplayNew","TryLoadKeybdSoundFromMemory","TryLoadKeybdSound","IsPureFileName","RemoveInvalidPathChars","ExportKeybdSound"}.Select(n=>Method(manager,n)));
        string data=Read("Features/Replay/Data/ReplayData.cs");
        if(!data.Contains("KeybdSoundSourcePath",StringComparison.Ordinal))
            data=data.Replace("public sealed class ReplayData\n{","public sealed class ReplayData\n{ internal string KeybdSoundSourcePath {get;set;} internal ReplaySaveOperation SaveOperation {get;set;}",StringComparison.Ordinal);
        string stop=Read("State/RecordingStop.cs");
        if(!stop.Contains("AudioPath",StringComparison.Ordinal))
            stop=stop.Replace("internal sealed class RecordingStop\n{","internal sealed class RecordingStop\n{ internal string AudioPath {get;set;}",StringComparison.Ordinal);
        string SaveCaller(string path, string name)
        {
            var caller = Type(path, name);
            return "static class " + name + " {\n" + string.Join("\n", caller.Members.OfType<FieldDeclarationSyntax>())
                + "\n" + caller.Members.OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "Postfix")
                    .WithAttributeLists(default).ToFullString() + "\n}";
        }
        string callers = SaveCaller("Features/Replay/Patches/Patch_Misc.cs", "Patch_ConductorUpdate")
            + SaveCaller("Features/Replay/Patches/Patch_Recording.cs", "Patch_OnLandOnPortal")
            + SaveCaller("Features/Replay/Patches/Patch_Recording.cs", "Patch_Fail2Action");
        string quitHook = string.Join("\n", Type("Main.cs", "Main").Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "Load").DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(s => s.Expression is AssignmentExpressionSyntax a && a.Left.ToString() == "Application.wantsToQuit"));
        string code=SaveCode.Replace("__CHECKS__",Checks).Replace("__CALLERS__",callers)
            .Replace("__STATE__",stateMembers).Replace("__MANAGER__",managerMembers)
            .Replace("__FINALIZE__",Method(recorder,"FinalizeRecording")).Replace("__QUIT_HOOK__", quitHook);
        string[] pure={"Features/Replay/Data/Replay.cs","Features/Replay/Data/ReplayConstants.cs",
            "Features/Replay/Data/KeybdAudioBlob.cs","Features/Replay/Data/KeybdPrefs.cs",
            "Features/Replay/Data/keyEvent.cs","Features/Replay/Data/HitSnapshot.cs","Features/Replay/Data/CursorSample.cs",
            "Features/Replay/Codec/ReplayCompression.cs","Features/Replay/Codec/ReplayEncoder.cs",
            "Features/Replay/Codec/ReplayDecoder.cs","Features/Replay/Codec/ReplayCrypto.cs",
            "Features/Replay/Codec/KeybdAudioCodec.cs","State/AtomicFile.cs",
            "Features/Replay/Runtime/ReplaySaveOperation.cs", "State/ReplaySaveShutdown.cs"};
        string[] sources = pure.Select(Read).Concat(new[]{data,stop}).ToArray();
        Test("F12/F14/F21/F28",code,sources);
        TestSaveExitProcess(code, sources);

    }

    private static void TestSaveExitProcess(string code, string[] sources)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ReplaySaveExit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
                .Where(p => !string.Equals(p, typeof(Driver).Assembly.Location, StringComparison.OrdinalIgnoreCase))
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("SaveExit", new[] { code }.Concat(sources)
                .Select(s => CSharpSyntaxTree.ParseText(s)), refs,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication));
            string image = Path.Combine(directory, "SaveExit.dll");
            var emitted = compilation.Emit(image);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("; ", emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Take(8)));
            File.WriteAllText(Path.Combine(directory, "SaveExit.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.0\"}}}");
            string output = Path.Combine(directory, "replays");
            var info = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            info.ArgumentList.Add(image);
            info.ArgumentList.Add(output);
            using var process = System.Diagnostics.Process.Start(info);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45000))
            {
                process.Kill();
                throw new TimeoutException("save/quit child did not finish");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException(stdout.GetAwaiter().GetResult()
                + stderr.GetAwaiter().GetResult());
            var audio = SpectreReplay.Features.Replay.ReplayDecoder.Decode(SpectreReplay.Features.Replay.ReplayCrypto.Decrypt(
                File.ReadAllBytes(Path.Combine(output, "quit-audio.sprp"))));
            bool ok = File.Exists(Path.Combine(output, "quit-normal.sprp")) && audio.KeybdAudio.HasData;
            Console.WriteLine("  " + (ok ? "✓ " : "✗ ") + "F28 独立子进程经真实退出门正常结束，父进程仍可读取完整回放和嵌入录音");
            if (!ok) failures++;
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("  ✗ F28 正常退出子进程验证失败: " + ex.Message);
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }
    private const string SaveCode = """
using System;using System.IO;using System.Linq;using System.Text;using System.Collections.Generic;
using System.Threading;using System.Security.Cryptography;using System.Reflection;
using SpectreReplay;using SpectreReplay.API;using SpectreReplay.Features.Replay;using SpectreReplay.Features.Cloud;
using UnityEngine;using Object=UnityEngine.Object;using static SpectreReplay.SpectreState;
public static class Fixture{
__CHECKS__
public static int MainId=Thread.CurrentThread.ManagedThreadId;
public static List<string> Saved=new(); public static List<int> EventThreads=new(); public static bool Reenter;
static string Root, RootOverride;
static ReplayData NewData(string name)=>new(){FileName=name,SongName="song",SceneName="scene",InternalLevelName="level",JudgeMode="Pure",
 HitSnapshots=new(){new HitSnapshot{CurrentFloorID=1}},KeyEvents=new(){new KeyEvent{KeyCode=65,IsPressed=true}},CursorTrack=new(){new CursorSample{X=0.5f}}};
static bool Wait(Func<bool> done){var end=DateTime.UtcNow.AddSeconds(15);while(DateTime.UtcNow<end){GameObject.TickAll();if(done())return true;Thread.Sleep(1);}return false;}
public static byte[] Wav(int seed){var b=new byte[32044];Encoding.ASCII.GetBytes("RIFF").CopyTo(b,0);BitConverter.GetBytes(b.Length-8).CopyTo(b,4);
 Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b,8);BitConverter.GetBytes(16).CopyTo(b,16);BitConverter.GetBytes((short)1).CopyTo(b,20);BitConverter.GetBytes((short)1).CopyTo(b,22);
 BitConverter.GetBytes(16000).CopyTo(b,24);BitConverter.GetBytes(32000).CopyTo(b,28);BitConverter.GetBytes((short)2).CopyTo(b,32);BitConverter.GetBytes((short)16).CopyTo(b,34);
 Encoding.ASCII.GetBytes("data").CopyTo(b,36);BitConverter.GetBytes(b.Length-44).CopyTo(b,40);for(int i=44;i<b.Length;i++)b[i]=(byte)(seed+i*7);return b;}
public static string Hash(byte[] w){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(w,44,w.Length-44)).Replace("-","").ToLowerInvariant();}
static Replay DecodeSaved(string path)=>ReplayDecoder.Decode(File.ReadAllBytes(File.Exists(path)?path:path+ReplayConstants.ExtensionNoEncrypt));
public static int Main(string[] args){
 try{Console.OutputEncoding=Encoding.UTF8;RootOverride=args[0];var result=Run();foreach(var line in result.Where(s=>s.StartsWith("✗")))Console.WriteLine(line);
  return Application.QuitAccepted&&result.All(s=>!s.StartsWith("✗"))?0:1;}
 catch(Exception ex){Console.WriteLine(ex);return 1;}}
public static string[] Run(){
 Root=RootOverride??Path.Combine(Path.GetTempPath(),"ShippingReplay-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Root);
 Settings.SavePath=Root;SpectreState.debug_no_encryption=true;
 var normal=NewData("normal");Saved.Clear();SpectreState.SaveFile(savedData:normal);var op=normal.SaveOperation;
 Wait(()=>Saved.Count==1);
 Check(Saved.Count==1&&Saved[0]==Path.GetFullPath(Path.Combine(Root,"normal.psprp"))&&File.Exists(Saved[0]),"F12 正常保存事件给出实际完整路径与格式后缀");
 Check(op!=null&&op.IsCompleted&&op.WorkerThreadId!=MainId&&EventThreads.All(t=>t==MainId),"F21 容器/键音编码与原子写盘在后台，结果仅主线程发布");
 Saved.Clear();EventThreads.Clear();var retry=NewData("invalid:name");SpectreState.SaveFile(savedData:retry);Wait(()=>Saved.Count==1);
 Check(Saved.Count==1&&Saved[0].EndsWith(".psprp",StringComparison.Ordinal)&&File.Exists(Saved[0])&&Path.GetFileName(Saved[0]).StartsWith("safe",StringComparison.Ordinal),"F12 非法名称失败后只发布安全重试的实际路径");
 Saved.Clear();EventThreads.Clear();byte[] first=Wav(1),next=Wav(2);string tmp=Path.Combine(Root,"tmp.wav");File.WriteAllBytes(tmp,first);
 var rec=new Recorder{currentFilePath=tmp,Expected=Hash(first)};var audio=NewData("audio");audio.KeybdSoundFileName="audio.wav";audio.KeybdSoundHash=Hash(first);audio.KeybdSoundStop=rec.LastStop;
 bool accepted=SpectreState.SaveFile(savedData:audio);audio.KeyEvents[0].KeyCode=66;audio.HitSnapshots[0].CurrentFloorID=9;audio.CursorTrack[0].X=0.9f;audio.Reset();rec.Seal();
 bool owned=rec.LastStop.AudioPath!=null&&File.Exists(rec.LastStop.AudioPath)&&!File.Exists(tmp);
 rec.Publish();rec.Publish();File.WriteAllBytes(tmp,next);Wait(()=>Saved.Count==1);Replay saved=Saved.Count==1?DecodeSaved(Saved[0]):null;
 Check(accepted&&owned&&saved!=null&&saved.KeybdAudio.HasData&&File.ReadAllBytes(tmp).SequenceEqual(next),"F21 封头后独占WAV、立即下一局写tmp不串音不误删，重复停止只保存一次");
 Check(saved!=null&&saved.KeyEvents.Count==1&&saved.KeyEvents[0].KeyCode==65&&saved.HitSnapshots[0].CurrentFloorID==1&&saved.CursorTrack[0].X==0.5f,"F21 排队后元素修改/Reset不污染独立保存快照");
 string blocked=Path.Combine(Root,"blocked");File.WriteAllBytes(blocked,new byte[]{1});string failedTmp=Path.Combine(Root,"failed.wav");File.WriteAllBytes(failedTmp,first);
 var failedRec=new Recorder{currentFilePath=failedTmp,Expected=Hash(first)};failedRec.Seal();failedRec.Publish();var failed=NewData("failed");failed.KeybdSoundFileName="failed.wav";failed.KeybdSoundHash=Hash(first);failed.KeybdSoundStop=failedRec.LastStop;
 int before=Saved.Count;SpectreState.SaveFile(savedData:failed,saveDirectory:blocked);var failedOp=failed.SaveOperation;Wait(()=>failedOp==null||failedOp.IsCompleted);GameObject.TickAll();
 Check(failedOp!=null&&!failedOp.Success&&Saved.Count==before&&File.Exists(failedRec.LastStop.AudioPath),"F21 主文件与安全重试均失败时不报成功，保留本局独占音频可重试");
 SpectreState.SaveFile(savedData:failed);Wait(()=>Saved.Count==before+1);
 Check(Saved.Count==before+1&&DecodeSaved(Saved.Last()).KeybdAudio.HasData,"F21 最终失败后仍可再次保存本局音频");
 for(int caller=0;caller<4;caller++){
  Settings.SavePath=blocked;Settings.DontSaveWhenMiss=caller==2;
  var owner=NewData("retry-caller"+caller);SpectreState.data=owner;SpectreState.PendingSave=true;SpectreState.is_recording=true;
  int count=Saved.Count;
  if(caller==0)Patch_ConductorUpdate.Postfix();else if(caller<3)Patch_OnLandOnPortal.Postfix(scrController.instance,(Portal)1,"");else Patch_Fail2Action.Postfix(scrController.instance);
  var rejected=owner.SaveOperation;bool keptQueued=SpectreState.PendingSave;
  Wait(()=>owner.SaveOperation==null);
  Check(keptQueued&&rejected!=null&&rejected.IsCompleted&&!rejected.Success&&SpectreState.PendingSave&&Saved.Count==count,
   "F21R 请求排队和两次写盘失败均保留待保存，真实调用方 "+caller);
  Settings.SavePath=Root;Patch_ConductorUpdate.Postfix();Wait(()=>owner.SaveOperation==null);
  Check(Saved.Count==count+1&&!SpectreState.PendingSave&&File.Exists(Saved.Last()),
   "F21R 修正目录后保存热键可重试，成功完成才清待保存，真实调用方 "+caller);
 }
 Settings.DontSaveWhenMiss=false;
 for(int reset=0;reset<2;reset++){
  var owner=NewData("late-owner"+reset);SpectreState.data=owner;SpectreState.PendingSave=true;
  SpectreState.SaveFile();var late=owner.SaveOperation;
  if(reset==0)SpectreState.data=NewData("next-owner");else owner.Reset();
  SpectreState.PendingSave=true;Wait(()=>late.IsCompleted);GameObject.TickAll();
  Check(late.Success&&SpectreState.PendingSave,"F21R 旧完成不清新会话待保存，替换或原地Reset "+reset);
 }
 var canceled=NewData("canceled-pending");SpectreState.data=canceled;Settings.SavePath=blocked;SpectreState.PendingSave=true;
 SpectreState.SaveFile();var canceledOp=canceled.SaveOperation;SpectreState.PendingSave=false;
 Wait(()=>canceledOp.IsCompleted);GameObject.TickAll();
 Check(!canceledOp.Success&&!SpectreState.PendingSave,"F21R 失败完成不重新开启用户取消的待保存");Settings.SavePath=Root;
 Saved.Clear();EventThreads.Clear();var unloaded=NewData("unloaded");SpectreState.SaveFile(savedData:unloaded);var unloadedOp=unloaded.SaveOperation;
 MainThread.Installed=false;Reenter=true;unloaded.Reset();Wait(()=>Saved.Count==1);GameObject.TickAll();GameObject.TickAll();
 Check(unloadedOp!=null&&unloadedOp.Success&&Saved.Count==1&&EventThreads.Single()==MainId,"F21 模拟卸载主调度泵/会话Reset后，独立观察器完成已接受保存且事件一次");
 string a=Path.Combine(Root,"A"),b=Path.Combine(Root,"B");Directory.CreateDirectory(a);Directory.CreateDirectory(b);File.WriteAllBytes(Path.Combine(a,"old.wav"),first);File.WriteAllBytes(Path.Combine(b,"old.wav"),next);
 var loaded=NewData("old");loaded.KeybdSoundFileName="old.wav";loaded.KeybdSoundHash=Hash(first);ReplayManager.LoadSound(Path.Combine(a,"old.sprp"),loaded);var snap=loaded.SnapshotForSave();loaded.Reset();Settings.SavePath=b;
 bool exported=ReplayManager.ExportKeybdSound(snap,out string exportedPath,out _);
 Check(exported&&File.ReadAllBytes(exportedPath).SequenceEqual(first),"F14 SavePath变更及原会话Reset后，Snapshot仍从已验证来源导出旧WAV而非同名错音频");
 snap.KeybdSoundMemory=null;bool fallback=ReplayManager.ExportKeybdSound(snap,out string fallbackPath,out _);
 Check(fallback&&File.ReadAllBytes(fallbackPath).SequenceEqual(first),"F14 缓存失效仍从已验证原路径读取并校验，不从新目录猜同名音频");
 var invalid=NewData("bad");invalid.KeybdSoundFileName="old.wav";invalid.KeybdSoundHash="bad";invalid.KeybdSoundMemory=first;ReplayManager.LoadSound(Path.Combine(a,"bad.sprp"),invalid);
 Check(invalid.KeybdSoundMemory==null&&!SpectreState.HasKeybdSound,"F14 哈希失败清理旧来源，不把上个会话音频留给导出");
 SpectreReplay.Main.InstallQuitHook();SpectreReplay.Main.InstallQuitHook();Saved.Clear();EventThreads.Clear();SpectreState.debug_no_encryption=false;Settings.SavePath=Root;
 int cloudBefore=CloudReplay.Uploads;
 string quitWav=Path.Combine(Root,"quit-source.wav");File.WriteAllBytes(quitWav,first);
 var quitRecorder=new Recorder{currentFilePath=quitWav,Expected=Hash(first)};
 var quitAudio=NewData("quit-audio");quitAudio.KeybdSoundFileName="quit-audio.wav";quitAudio.KeybdSoundHash=Hash(first);quitAudio.KeybdSoundStop=quitRecorder.LastStop;
 bool quitAcceptedRequest=SpectreState.SaveFile(savedData:quitAudio);var quitAudioOperation=quitAudio.SaveOperation;
 var quitNormal=NewData("quit-normal");
 object saveLock=typeof(ReplaySaveOperation).GetField("SaveLock",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
 bool deferred;
 lock(saveLock){SpectreState.SaveFile(savedData:quitNormal);Application.Quit();Application.Quit();
  deferred=quitAcceptedRequest&&!Application.QuitAccepted&&Application.QuitRequests==2&&ReplaySaveOperation.HasPendingSaves;}
 Wait(()=>quitNormal.SaveOperation==null);
 deferred&=!Application.QuitAccepted&&Application.QuitRequests==2&&ReplaySaveOperation.HasPendingSaves&&Saved.Count==1;
 quitRecorder.Seal();quitRecorder.Publish();Wait(()=>Application.QuitAccepted);
 bool quitFinished=deferred&&Application.QuitAccepted&&Application.QuitRequests==3&&Application.QuitHandlerCalls==3
  &&Saved.Count==2&&CloudReplay.Uploads==cloudBefore+2&&!ReplaySaveOperation.HasPendingSaves
  &&File.Exists(Path.Combine(Root,"quit-normal.sprp"))
  &&ReplayDecoder.Decode(ReplayCrypto.Decrypt(File.ReadAllBytes(Path.Combine(Root,"quit-audio.sprp")))).KeybdAudio.HasData;
 Check(quitFinished,
  "F28 退出等待排队写盘及录音封头，完成事件/云入口后仅重试退出一次，重复装配不重复订阅"
  +(quitFinished?"":$" (deferred={deferred}, quit={Application.QuitAccepted}, requests={Application.QuitRequests}, handlers={Application.QuitHandlerCalls}, saved={Saved.Count}, cloud={CloudReplay.Uploads-cloudBefore}, pending={ReplaySaveOperation.HasPendingSaves}, normal={File.Exists(Path.Combine(Root,"quit-normal.sprp"))}, audio={quitAudioOperation.SavedPath}, audioFailed={quitAudioOperation.AudioFailed}, error={quitAudioOperation.Error})"));
 Application.Reset();GameObject.FailCreate=true;bool queuedFailure=SpectreState.SaveFile(savedData:NewData("queue-failed"));GameObject.FailCreate=false;
 Application.Quit();
 Check(!queuedFailure&&!ReplaySaveOperation.HasPendingSaves&&Application.QuitAccepted,
  "F28 保存排队失败释放退出凭据，不让退出永久等待无法启动的任务");
 Application.Reset();
 var manualQuit=new ReplaySaveOperation(NewData("manual-quit"),Root,"safe",false,false,false,null);
 manualQuit.Accept();Application.Quit();manualQuit.Release();
 Application.Quit(); // 主动重试已经过其它退出处理器；旧自动重试不能再弹一次退出确认。
 int manualRequests=Application.QuitRequests;GameObject.TickAll();
 bool noStaleQuit=Application.QuitRequests==manualRequests;
 manualQuit.Accept();Application.Quit();manualQuit.Release();Application.Quit();
 var nextQuit=new ReplaySaveOperation(NewData("next-quit"),Root,"safe",false,false,false,null);
 nextQuit.Accept();Application.Quit();GameObject.TickAll();bool singleWaiter=GameObject.PendingQuitCount==1;nextQuit.Release();
 int nextRequests=Application.QuitRequests;GameObject.TickAll();
 Check(noStaleQuit&&singleWaiter&&Application.QuitRequests==nextRequests+1&&Application.QuitAccepted,
  "F29 主动退出取代待办自动重试，旧等待器不能在新一轮退出请求中复活");
 return Lines.ToArray();}
}
namespace SpectreReplay{
static class Settings{public static string SavePath;public static bool KeybdSoundCompact,Backup,DontSaveWhenAuto,DontSaveWhenMiss,CompleteSave,LateSave;public static bool AutoSave=true,FailSave=true;public static int KeybdSoundVolume=10,MicrophoneOffset;}
static class Main{public static Mod mod=new();internal static void InstallQuitHook(){__QUIT_HOOK__}}class Mod{public string Path=System.IO.Path.GetTempPath();}
static class MainThread{public static bool Installed=true;public static bool IsMainThread=>Thread.CurrentThread.ManagedThreadId==Fixture.MainId;public static void Run(Action a){if(Installed)a();}}
static class LocalizationManager{public static string GetLocalizedText(string s)=>s;}
static class SpectrePatchHub{public static void EnsureApplied(string s,params Type[] t){}public static void ForceRebuild(Type t){}}
class Patch_Hit{}class Patch_UpdateInputConsumeAsync{}class Patch_PlayerControlUpdate{}class Patch_Won_Update{}
static class SpectreState{public static ReplayData data;public static bool EmptySaveBlocked,DebugMode,debug_skip_Verification,debug_no_encryption,HasKeybdSound,PendingSave,LateSaveMode,is_recording,is_playing;
 public static int save_button;public static int[] HitMarginCounts=new int[1];
 public static class PlayActions{public static void StopRecording(){is_recording=false;}public static void StopPlaying(){is_playing=false;}}
 static int name;static string CreateFileName(bool safe_mode=false)=>(safe_mode?"safe":"name")+(++name);internal static void NotifySaveSkipped(string a,string b){}internal static void TriggerMessage(string s){}
 __STATE__
}
__CALLERS__
enum States{Fail=5,Fail2=6,Won=7}enum Portal{Exit=1}
class scrController{public static scrController instance=new();public Machine stateMachine=new();public sealed class Machine{public object GetState()=>States.Won;}}
static class Input{public static bool GetKeyDown(int key)=>true;}
static class ManualSaveHotkey{public static bool TryConsume()=>true;}
static class HitMarginCompat{public static int FailCount(int[] counts)=>0;}
static class RDC{public static bool auto;}
static class SceneManager{public static Scene GetActiveScene()=>new();public sealed class Scene{public string name="scene";}}
class Recorder{public Thread initThread,processingThread;public object bufferLock=new(),hashLock=new();public FileStream fileStream;public List<byte> wavDataBuffer=new();public HashAlgorithm hashAlgorithm;
 public RecordingStop LastStop=new();public string finalizedHash,currentFilePath,Expected;void CloseFileStreamQuietly(){}string FinalizeWavFile()=>Expected;
 __FINALIZE__
 public void Seal()=>FinalizeRecording();public void Publish()=>LastStop.Complete(finalizedHash);}
static class AudioRecorder{public static bool VerifyAudioData(byte[] w,string h)=>w!=null&&w.Length>44&&Fixture.Hash(w)==h;}
static class WavLoader{public static AudioClip LoadFromMemory(byte[] w,string n)=>new();public static void ReplaceLoadedClip(AudioClip c){}}
}
namespace SpectreReplay.API{static class SpectreAPI{public static void RaiseReplaySaved(string p){Fixture.Saved.Add(p);Fixture.EventThreads.Add(Thread.CurrentThread.ManagedThreadId);if(Fixture.Reenter){Fixture.Reenter=false;GameObject.TickAll();}}}}
namespace SpectreReplay.Features.Cloud{static class CloudReplay{public static int Uploads;public static bool OnReplaySaved(string p,string w){Uploads++;return false;}}}
namespace SpectreReplay.Ui{static class Loc{public static string T(string s)=>s;}}
namespace SpectreReplay.Features.Replay{static class ReplayManager{const int MaxEmbedPayloadBytes=4*1024*1024;
static void TryLoadKeybdSoundFromBlob(ReplayData d,string p){}public static void LoadSound(string p,ReplayData d)=>TryLoadKeybdSound(p,d);
__MANAGER__
}
static class WavAudioTransform{public static bool TryApply(byte[] w,int v,int o,bool m,out byte[] b,out string error){b=(byte[])w.Clone();error=null;return true;}}}
class Preset{public int inputOffset;}class scrConductor{public static Preset currentPreset=new();}static class ADOBase{public static scrConductor conductor;}
namespace UnityEngine{public class MonoBehaviour{public GameObject gameObject;}public class AudioClip{}
public class GameObject{public static bool FailCreate;public GameObject(string n){if(FailCreate)throw new InvalidOperationException("cannot create observer");}static List<MonoBehaviour> all=new();public T AddComponent<T>() where T:MonoBehaviour{var c=(T)Activator.CreateInstance(typeof(T),true);c.gameObject=this;all.Add(c);return c;}
public static int PendingQuitCount=>all.Count(c=>c.GetType().Name=="PendingQuit");
public static void TickAll(){foreach(var c in all.ToArray())c.GetType().GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic)?.Invoke(c,null);}public static void Remove(GameObject o)=>all.RemoveAll(c=>c.gameObject==o);}
public static class Object{public static void DontDestroyOnLoad(GameObject o){}public static void Destroy(GameObject o)=>GameObject.Remove(o);}
public static class Application{public static event Func<bool> wantsToQuit;public static int QuitRequests,QuitHandlerCalls;public static bool QuitAccepted;
 public static void Quit(){QuitRequests++;QuitAccepted=true;foreach(Func<bool> handler in wantsToQuit?.GetInvocationList()??Array.Empty<Delegate>()){QuitHandlerCalls++;if(!handler())QuitAccepted=false;}}
 public static void Reset(){QuitRequests=QuitHandlerCalls=0;QuitAccepted=false;}}
public static class Debug{public static void Log(object s){}public static void LogWarning(object s){}public static void LogError(object s){}}}
""";

}
