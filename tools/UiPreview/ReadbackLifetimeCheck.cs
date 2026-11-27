using System;

namespace UiPreview;

// Compile the complete production frame source. Only the GPU/Unity boundary is replaced;
// successful native results expire on the next simulated frame, as Unity specifies.
internal static class ReadbackLifetimeCheck
{
    internal static int Run()
        => RenderRepairCheck.RunFixture("ReadbackLifetime",
            SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/FrameBufferOwnership.cs"),
            SrcUtil.Source(Driver.ProjectDir, "Features/VideoRender/Pipeline/VideoFrameSource.cs"),
            Boundary, Fixture);

    private const string Boundary = """
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object
    {
        public static void Destroy(Object value) { }
        public static void DontDestroyOnLoad(Object value) { }
    }
    public class MonoBehaviour : Object { }
    public class GameObject : Object
    {
        public GameObject(string name) { }
        public T AddComponent<T>() => (T)Activator.CreateInstance(typeof(T), true);
    }
    public enum RenderTextureFormat { ARGB32, ARGB64, ARGBHalf }
    public enum TextureFormat { RGBA32 }
    public enum CameraClearFlags { SolidColor }
    public struct Color { public static Color black => default; }
    public struct Vector3 { public float x, y; }
    public static class Input { public static Vector3 mousePosition; public static bool mousePresent = true; }
    public static class Screen { public static int width = 2, height = 1; }
    public class RenderTexture : Object
    {
        public RenderTexture(int w, int h, int depth, RenderTextureFormat format) { }
        public bool Create() => true;
        public void Release() { }
    }
    public class Camera : Object
    {
        public RenderTexture targetTexture;
        public CameraClearFlags clearFlags;
        public Color backgroundColor;
        public int cullingMask;
        public float depth, orthographicSize;
        public bool orthographic, allowHDR, allowMSAA;
    }
    public static class Graphics { public static void Blit(RenderTexture a, RenderTexture b) { } }
    public static class Debug { public static void LogException(Exception error) { } }
}
namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat { R16G16B16A16_SFloat, R16G16B16A16_UNorm }
}
namespace Unity.Collections
{
    public struct NativeArray<T>
    {
        private readonly T[] data;
        private readonly bool throwOnCopy;
        public NativeArray(T[] data, bool throwOnCopy) { this.data = data; this.throwOnCopy = throwOnCopy; }
        public int Length => data.Length;
        public void CopyTo(T[] destination)
        {
            if (throwOnCopy) throw new InvalidOperationException("controlled native copy failure");
            Array.Copy(data, destination, data.Length);
        }
    }
}
namespace UnityEngine.Rendering
{
    public sealed class Pending
    {
        public bool Done, Error, Expired, ThrowOnCopy;
        public byte[] Data;
        public Action<AsyncGPUReadbackRequest> Callback;
    }
    public struct AsyncGPUReadbackRequest
    {
        internal Pending State;
        public bool done => State.Done;
        public bool hasError => State.Error || State.Expired;
        public Unity.Collections.NativeArray<T> GetData<T>()
        {
            if (hasError || !done) throw new InvalidOperationException("native data unavailable");
            AsyncGPUReadback.NativeReads++;
            return new Unity.Collections.NativeArray<T>((T[])(object)State.Data, State.ThrowOnCopy);
        }
    }
    public static class AsyncGPUReadback
    {
        public static readonly List<Pending> Requests = new();
        public static int NativeReads;
        private static AsyncGPUReadbackRequest Add(Action<AsyncGPUReadbackRequest> callback)
        {
            var pending = new Pending { Callback = callback };
            Requests.Add(pending);
            return new AsyncGPUReadbackRequest { State = pending };
        }
        public static AsyncGPUReadbackRequest Request(UnityEngine.RenderTexture t, int mip,
            UnityEngine.TextureFormat f, Action<AsyncGPUReadbackRequest> callback) => Add(callback);
        public static AsyncGPUReadbackRequest Request(UnityEngine.RenderTexture t, int mip,
            UnityEngine.Experimental.Rendering.GraphicsFormat f, Action<AsyncGPUReadbackRequest> callback) => Add(callback);
        public static void Complete(int index, byte value, int length = 8, bool error = false, bool throwOnCopy = false)
        {
            var p = Requests[index];
            p.Done = true; p.Error = error; p.ThrowOnCopy = throwOnCopy;
            p.Data = new byte[length]; Array.Fill(p.Data, value);
            p.Callback(new AsyncGPUReadbackRequest { State = p });
        }
        public static void NextFrame()
        {
            foreach (var p in Requests)
                if (p.Done) { p.Expired = true; Array.Fill(p.Data, (byte)0xEE); }
        }
        public static void Reset() { Requests.Clear(); NativeReads = 0; }
    }
}
public sealed class scrCamera
{
    public static scrCamera instance = new();
    public UnityEngine.Camera Bgcamstatic = new(), BGcam = new(), camobj = new();
}
namespace SpectreReplay.Features.Replay
{
    public struct CursorSample { }
    public static class CursorTrackSampler
    {
        public static bool Sample(IReadOnlyList<CursorSample> track, double song, out float x, out float y)
        { x = y = 0; return false; }
    }
}
namespace SpectreReplay.Features.VideoRender
{
    internal static class VideoRender
    {
        public static VideoSurface? CurrentSurface;
        public static bool TryReadPinnedPosition(out double song) { song = 0; return false; }
    }
    public static class RenderOverlayCapture
    {
        public static void InstallClockOverride() { }
        public static void InstallCameraCapture(UnityEngine.Camera camera) { }
        public static void ReapplyIfNeeded() { }
        public static void Restore() { }
    }
    public static class ScreenCapture
    { public static void CaptureScreenshotIntoRenderTexture(UnityEngine.RenderTexture texture) { } }
    public sealed class SystemCursorImage { }
    public static class SystemCursor
    {
        public enum CursorAcquire { Hidden, Failed }
        public static CursorAcquire TryAcquire(out SystemCursorImage image)
        { image = null; return CursorAcquire.Failed; }
    }
    public static class CursorOverlay
    {
        public static bool IsSystemCursorHidden() => false;
        public static void Draw(byte[] pixels, int width, int height, bool flip,
            float x, float y, float sw, float sh) { pixels[0] = (byte)x; }
        public static void Draw(byte[] pixels, int width, int height, bool flip, SystemCursorImage cursor,
            float x, float y, float sw, float sh) { pixels[0] = (byte)x; }
    }
}
""";

    private const string Fixture = """
using System;
using System.Collections.Generic;
using SpectreReplay.Features.VideoRender;
using UnityEngine.Rendering;
public static class RenderRepairFixture_ReadbackLifetime
{
    private static void Check(List<string> r, bool ok, string label)
        => r.Add((ok ? "✓ " : "✗ ") + label);
    private static bool Take(VideoFrameSource source, int expectedIndex, byte expectedValue, out byte[] pixels)
    {
        bool ready = source.TryTake(out int index, out pixels, out bool failed);
        return ready && !failed && index == expectedIndex && pixels != null && pixels[0] == expectedValue;
    }
    public static string[] Run()
    {
        var r = new List<string>();
        AsyncGPUReadback.Reset();
        var source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1);
        source.Grab(10);
        AsyncGPUReadback.Complete(0, 0x31);
        AsyncGPUReadback.NextFrame(); // Encoding backpressure: native result is already disposed.
        Check(r, Take(source, 10, 0x31, out byte[] delayed) && source.SubstituteMissing == 0,
            "RF01 成功回读跨帧等待编码器后仍交付原像素，不变黑帧");
        if (delayed != null) source.RecycleOrHold(delayed);
        source.Dispose();

        AsyncGPUReadback.Reset();
        source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1);
        source.Grab(0);
        AsyncGPUReadback.Complete(0, 0x11);
        bool seed = Take(source, 0, 0x11, out byte[] first);
        source.RecycleOrHold(first);
        source.Grab(1);
        AsyncGPUReadback.Complete(1, 0x22);
        AsyncGPUReadback.NextFrame();
        Check(r, seed && Take(source, 1, 0x22, out _) && source.SubstituteCount == 0,
            "RF02 延迟消费的成功帧不得错误顶替成上一帧");
        source.Dispose();

        AsyncGPUReadback.Reset();
        source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1);
        source.Grab(0); source.Grab(1);
        AsyncGPUReadback.Complete(1, 0x42);
        bool waitsForHead = !source.TryTake(out _, out _, out _);
        AsyncGPUReadback.NextFrame();
        AsyncGPUReadback.Complete(0, 0x41);
        bool head = Take(source, 0, 0x41, out byte[] orderedFirst);
        source.RecycleOrHold(orderedFirst);
        Check(r, waitsForHead && head && Take(source, 1, 0x42, out _) && source.SubstituteCount == 0,
            "RF03 乱序完成保持FIFO，先完成的后序帧也不因等待队首过期");
        source.Dispose();

        AsyncGPUReadback.Reset();
        source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1);
        source.Grab(0); source.Grab(1); source.Grab(2);
        AsyncGPUReadback.Complete(0, 0x71);
        AsyncGPUReadback.Complete(2, 0x73); // Later success must not become frame 1's fallback.
        AsyncGPUReadback.Complete(1, 0, error: true);
        bool deliveredFirst = Take(source, 0, 0x71, out first);
        source.RecycleOrHold(first);
        bool substituted = Take(source, 1, 0x71, out byte[] fallback);
        source.Recycle(fallback);
        Check(r, deliveredFirst && substituted && Take(source, 2, 0x73, out _)
                && source.SubstituteCount == 1 && source.SubstituteMissing == 0,
            "RF04 真失败只顶替最近已交付的帧，不能用先完成的未来帧");
        source.Dispose();

        AsyncGPUReadback.Reset();
        source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1);
        source.Grab(0);
        source.Dispose();
        int readsBeforeCloseCallback = AsyncGPUReadback.NativeReads;
        AsyncGPUReadback.Complete(0, 0x51);
        Check(r, AsyncGPUReadback.NativeReads == readsBeforeCloseCallback
                && source.InFlight == 0 && !source.TryTake(out _, out _, out _),
            "RF05 关闭后晚到回调不访问原生像素或复活队列");

        bool precision = true;
        foreach (int mode in new[] { 0, 1, 2 })
        {
            AsyncGPUReadback.Reset();
            source = new VideoFrameSource(VideoSurface.CleanCamera, 2, 1, tenBit: mode == 1, hdr10: mode == 2);
            source.Grab(0);
            AsyncGPUReadback.Complete(0, 0x61, length: mode == 0 ? 8 : 16);
            AsyncGPUReadback.NextFrame();
            bool ok = Take(source, 0, 0x61, out byte[] pixels);
            precision &= ok && pixels.Length == (mode == 0 ? 8 : 16) && source.NeedsRowFlip;
            source.RecycleOrHold(pixels);
            source.Dispose();
        }
        Check(r, precision, "RF06 8-bit/16-bit/HDR半浮点保留字节数与行序，成功结果不依赖request寿命");

        AsyncGPUReadback.Reset();
        source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1) { DrawCursor = true };
        UnityEngine.Input.mousePosition = new UnityEngine.Vector3 { x = 7 };
        source.Grab(0);
        UnityEngine.Input.mousePosition = new UnityEngine.Vector3 { x = 9 };
        AsyncGPUReadback.Complete(0, 0x21);
        AsyncGPUReadback.NextFrame();
        Check(r, Take(source, 0, 7, out _), "RF07 延迟交付仍使用捕获帧的光标位置");
        source.Dispose();

        AsyncGPUReadback.Reset();
        source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1);
        source.Grab(0);
        AsyncGPUReadback.Complete(0, 0x81);
        bool beforeCopyFailure = Take(source, 0, 0x81, out first);
        source.RecycleOrHold(first);
        source.Grab(1);
        AsyncGPUReadback.Complete(1, 0x82, throwOnCopy: true);
        Check(r, beforeCopyFailure && Take(source, 1, 0x81, out fallback) && source.SubstituteCount == 1,
            "RF08 原生复制异常不发布半成品或外抛，仍顶替最近成功帧");
        source.Recycle(fallback);
        source.Dispose();

        AsyncGPUReadback.Reset();
        source = new VideoFrameSource(VideoSurface.WholeScreen, 2, 1);
        source.Grab(0);
        AsyncGPUReadback.Complete(0, 0x91, length: 4);
        bool invalid = source.TryTake(out int invalidIndex, out byte[] invalidPixels, out bool invalidFailed);
        source.Grab(1);
        AsyncGPUReadback.Complete(1, 0x92);
        Check(r, invalid && invalidIndex == 0 && invalidFailed && invalidPixels == null
                && source.SubstituteMissing == 1 && Take(source, 1, 0x92, out _),
            "RF09 长度异常仍按失败交付且不污染后续成功帧");
        source.Dispose();
        return r.ToArray();
    }
}
""";
}
