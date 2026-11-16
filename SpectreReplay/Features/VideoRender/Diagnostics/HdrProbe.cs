using System;
using System.Collections;
using System.Collections.Generic;
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
                // 2B-1 对拍：同一相机链紧邻两帧分别渲到 ARGB32 / ARGB64，统计每通道
                // 唯一量化值数与相邻亮度阶差。帧间画面差在慢速段落可接受——要量的
                // 是量化阶数分布，不是逐像素绝对值。
                yield return CaptureAbReadback(camera, rt, tex, w, h);
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

        /// <summary>
        /// 2B-1 banding 对拍：同一相机链抓两帧，ARGB32（8-bit，对应现状）与
        /// ARGB64（10-bit，对应 2B-1）。统计每通道唯一量化值数（uniq）与
        /// 亮度相邻差的非零比（gradient walk），写进 ab_* 字段。
        /// </summary>
        private IEnumerator CaptureAbReadback(scrCamera camera, RenderTexture rtHalf,
            Texture2D texHalf, int w, int h)
        {
            var abRt = new[]
            {
                new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32),
                new RenderTexture(w, h, 24, RenderTextureFormat.ARGB64),
            };
            var uniq = new[] { -1, -1 };
            var steps = new[] { -1, -1 };
            var pngs = new[] { "", "" };
            string dir = Application.persistentDataPath;
            Directory.CreateDirectory(dir);
            for (int k = 0; k < 2; k++)
            {
                if (!abRt[k].Create())
                {
                    WriteReport("ab rt create failed", 0f, -1, 0, 0, 0, null);
                    break;
                }
                camera.Bgcamstatic.targetTexture = abRt[k];
                camera.BGcam.targetTexture = abRt[k];
                camera.camobj.targetTexture = abRt[k];
                yield return new WaitForEndOfFrame();
                TextureFormat tf = k == 0 ? TextureFormat.RGBA32 : TextureFormat.RGBA64;
                Texture2D t = new Texture2D(w, h, tf, false, true);
                RenderTexture.active = abRt[k];
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t.Apply(false);
                try
                {
                    CountQuantization(t, k == 1, out uniq[k], out steps[k]);
                    string p = Path.Combine(dir, "hdr-probe-ab" + (k == 0 ? "8" : "10") + ".png");
                    File.WriteAllBytes(p, t.EncodeToPNG());
                    pngs[k] = p;
                }
                finally
                {
                    Destroy(t);
                    RenderTexture.active = null;
                    abRt[k].Release();
                    Destroy(abRt[k]);
                }
                yield return null;
            }
            // 恢复相机目标（finally 之外的兜底；正常采样已恢复过）
            camera.Bgcamstatic.targetTexture = rtHalf;
            camera.BGcam.targetTexture = rtHalf;
            camera.camobj.targetTexture = rtHalf;
            AppendAbReport(uniq[0], steps[0], uniq[1], steps[1], pngs[0], pngs[1]);
            // rtHalf/texHalf 归还调用处的 finally；这里只收尾本协程的资源
        }

        /// <summary>统计唯一量化值数（每通道合并集合）与亮度相邻差非零比。</summary>
        private static void CountQuantization(Texture2D t, bool tenBit, out int uniq, out int steps)
        {
            int w = t.width, h = t.height;
            var seen = new HashSet<int>();
            int nonzeroSteps = 0, totalSteps = 0;
            Color32[] c32 = tenBit ? null : t.GetPixels32();
            Color[] c = tenBit ? t.GetPixels() : null;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    int r, g, b;
                    if (tenBit) { r = (int)(c[idx].r * 65535f); g = (int)(c[idx].g * 65535f); b = (int)(c[idx].b * 65535f); }
                    else { r = c32[idx].r; g = c32[idx].g; b = c32[idx].b; }
                    seen.Add(r); seen.Add(g); seen.Add(b);
                    if (x > 0)
                    {
                        int pr, pg, pb;
                        if (tenBit) { pr = (int)(c[idx - 1].r * 65535f); pg = (int)(c[idx - 1].g * 65535f); pb = (int)(c[idx - 1].b * 65535f); }
                        else { pr = c32[idx - 1].r; pg = c32[idx - 1].g; pb = c32[idx - 1].b; }
                        // 亮度近似：只看 R 通道的相邻差（渐变区主要看单通道阶差）
                        int d = Math.Abs(r - pr);
                        totalSteps++;
                        if (d != 0) nonzeroSteps++;
                    }
                }
            }
            uniq = seen.Count;
            steps = totalSteps == 0 ? 0 : nonzeroSteps * 100 / totalSteps;
        }

        /// <summary>把 ab_* 追加到 hdr-probe.txt（独立写，避免重排既有字段）。</summary>
        private static void AppendAbReport(int uniq8, int steps8, int uniq10, int steps10,
            string png8, string png10)
        {
            string dir = Application.persistentDataPath;
            Directory.CreateDirectory(dir);
            string text = "ab_uniq8=" + uniq8
                + "\nab_steps8=" + steps8
                + "\nab_uniq10=" + uniq10
                + "\nab_steps10=" + steps10
                + "\nab_png8=" + (png8 ?? "")
                + "\nab_png10=" + (png10 ?? "")
                + "\n";
            File.AppendAllText(Path.Combine(dir, "hdr-probe.txt"), text);
            Debug.Log("[SpectreReplay] HDR probe AB: " + text);
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