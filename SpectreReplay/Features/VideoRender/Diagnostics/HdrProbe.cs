using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace SpectreReplay.Features.VideoRender
{
    /// <summary>
    /// 2B-0 颜色先导实验：命令行参数 <c>--spectre-hdr-probe</c> 启动时，
    /// 等歌曲真正在播后用 CleanCamera 的三台相机渲染，回读到 ARGBHalf（浮点 16-bit），
    /// 统计 RGB 通道最大值与超过 1.0 的像素数，并把 PNG + 文本报告写到
    /// <c>Application.persistentDataPath</c>。
    ///
    /// **为什么是 ARGBHalf 而不是 ARGB64。** ARGB64 是 16-bit UNorm，会把 >1.0
    /// /HDR 值钳到 1.0；用它能看到 banding 改善，但永远测不出“有没有超白”。
    /// 本探针只回答 2B-0 的一个问题：ADOFAI 的 SDR 美术在 CleanCamera 下是否
    /// 真的能超过 1.0。若最大值 ≤ 1.0，2B 就降级为“10-bit 精度”，不宣称 HDR。
    ///
    /// **必须等歌曲真正在播。** 选曲界面也有 scrCamera 与 scrConductor，只判
    /// instance != null 会在菜单抓到一张暗帧，得出 max ≤ 1.0 的假阴性。
    /// </summary>
    internal static class HdrProbe
    {
        private const string Arg = "--spectre-hdr-probe";
        private static GameObject _host;

        internal static void TryStart()
        {
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, Arg, StringComparison.OrdinalIgnoreCase))
                {
                    if (_host != null) return;
                    _host = new GameObject("SpectreHdrProbe");
                    UnityEngine.Object.DontDestroyOnLoad(_host);
                    _host.AddComponent<HdrProbeRunner>();
                    return;
                }
            }
        }

        internal static void Stop()
        {
            if (_host == null) return;
            UnityEngine.Object.Destroy(_host);
            _host = null;
        }
    }

    internal sealed class HdrProbeRunner : MonoBehaviour
    {
        private IEnumerator Start()
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            scrConductor conductor = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                conductor = scrConductor.instance;
                scrCamera cam = scrCamera.instance;
                if (conductor != null && conductor.song != null && conductor.song.isPlaying
                    && cam != null && cam.camobj != null && cam.Bgcamstatic != null && cam.BGcam != null)
                    break;
                yield return null;
            }

            scrCamera camera = scrCamera.instance;
            if (conductor == null || conductor.song == null || !conductor.song.isPlaying
                || camera == null || camera.camobj == null || camera.Bgcamstatic == null || camera.BGcam == null)
            {
                WriteReport("no playing song / scrCamera unavailable", 0f, -1, 0, 0, 0, null);
                Destroy(gameObject);
                yield break;
            }

            const int w = 1280, h = 720;
            RenderTexture rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBHalf);
            if (!rt.Create())
            {
                WriteReport("rt create failed", 0f, -1, 0, 0, 0, null);
                Destroy(rt);
                Destroy(gameObject);
                yield break;
            }

            RenderTexture oldStatic = camera.Bgcamstatic.targetTexture;
            RenderTexture oldBg = camera.BGcam.targetTexture;
            RenderTexture oldMain = camera.camobj.targetTexture;
            Texture2D tex = null;
            try
            {
                camera.Bgcamstatic.targetTexture = rt;
                camera.BGcam.targetTexture = rt;
                camera.camobj.targetTexture = rt;
            }
            catch (Exception ex)
            {
                camera.Bgcamstatic.targetTexture = oldStatic;
                camera.BGcam.targetTexture = oldBg;
                camera.camobj.targetTexture = oldMain;
                rt.Release();
                Destroy(rt);
                WriteReport("assign exception: " + ex.Message, 0f, -1, 0, 0, 0, null);
                Debug.LogException(ex);
                Destroy(gameObject);
                yield break;
            }

            // 采样 2.5 秒 / 最多 180 帧：判定闪光、粒子、拖尾可能只在某一帧出现。
            // 每帧都读回，逐帧累加全局 max 与 nonblack；单帧暗帧不能代表整关。
            float max = 0f;
            int maxFrame = -1;
            int over1 = 0;
            int nonblack = 0;
            int frames = 0;
            float end = Time.realtimeSinceStartup + 2.5f;
            try
            {
                tex = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
                while (Time.realtimeSinceStartup < end && frames < 180)
                {
                    yield return new WaitForEndOfFrame();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    tex.Apply(false);
                    Color[] px = tex.GetPixels();
                    float frameMax = 0f;
                    int frameNonblack = 0;
                    for (int i = 0; i < px.Length; i++)
                    {
                        Color c = px[i];
                        float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                        if (m > frameMax) frameMax = m;
                        if (c.r > 0f || c.g > 0f || c.b > 0f) frameNonblack++;
                        if (c.r > 1f || c.g > 1f || c.b > 1f) over1++;
                    }
                    if (frameMax > max)
                    {
                        max = frameMax;
                        maxFrame = frames;
                    }
                    nonblack += frameNonblack;
                    frames++;
                }

                string dir = Application.persistentDataPath;
                Directory.CreateDirectory(dir);
                string png = Path.Combine(dir, "hdr-probe.png");
                File.WriteAllBytes(png, tex.EncodeToPNG());
                string status = nonblack == 0 ? "nonblack=0" : "ok";
                WriteReport(status, max, maxFrame, over1, nonblack, frames, png);
            }
            finally
            {
                camera.Bgcamstatic.targetTexture = oldStatic;
                camera.BGcam.targetTexture = oldBg;
                camera.camobj.targetTexture = oldMain;
                RenderTexture.active = null;
                if (tex != null) Destroy(tex);
                rt.Release();
                Destroy(rt);
            }
            Destroy(gameObject);
        }

        private static void WriteReport(string status, float max, int maxFrame, int over1,
            int nonblack, int frames, string png)
        {
            string dir = Application.persistentDataPath;
            Directory.CreateDirectory(dir);
            string text = "status=" + status
                + "\nmax=" + max.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)
                + "\nmax_frame=" + maxFrame
                + "\nover1=" + over1
                + "\nnonblack=" + nonblack
                + "\nframes=" + frames
                + "\npng=" + (png ?? "")
                + "\nscreen=" + Screen.width + "x" + Screen.height
                // Linear=1 / Gamma=0。接 PQ 前必须知道：Gamma 空间的值是 gamma 编码，
                // 直接标 smpte2084 是错的（2B 计划里明令禁止的那条）。
                + "\ncolorspace=" + (int)QualitySettings.activeColorSpace
                + "\n";
            File.WriteAllText(Path.Combine(dir, "hdr-probe.txt"), text);
            Debug.Log("[SpectreReplay] HDR probe: " + text);
        }
    }
}