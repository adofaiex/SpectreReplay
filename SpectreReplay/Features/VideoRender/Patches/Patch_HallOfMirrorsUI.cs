// 共享库：SpectrePatch 属性 / PatchType / PatchPriority
global using SpectreReplay.PatchShared;
using System;
using System.Collections.Generic;
using UnityEngine;
using static SpectreReplay.SpectreState;

namespace SpectreReplay.Features.VideoRender;

// ── 镜厅罩 UI ────────────────────────────────────────────────────────
//
// 镜厅（Hall of Mirrors）的本体机制（scrController.EnableHallOfMirrors，
// scrController.cs:3096）：把 Bgcamstatic 的 clearFlags 从 Color 换成 Depth——
// **相机不清屏，画面逐帧累积**，被这几台相机渲染的内容全部留下拖尾。
// 游戏的 UI 画布（scrUIController.canvas）是 ScreenSpaceOverlay：不经过任何
// 相机，直接画在最终画面最上层，所以累积罩不到它——镜厅开着时 UI 永远清晰
// 浮在拖尾之上，与游戏世界的镜像观感割裂。
//
// 本补丁在镜厅开启时把全部 ScreenSpaceOverlay 根画布迁到一台**同样不清屏**
// 的专属相机上（ScreenSpaceCamera + clearFlags=Depth），UI 每帧画在上一次的
// 残留上——UI 与游戏世界一起进入镜厅。镜厅关闭或离开关卡时完整还原。
//
// 范围裁定（实测后放宽）：迁**当前全部 ScreenSpaceOverlay 根画布**——游戏
// UI 与模组 UI（键位显示等）一起进镜厅；初版只迁游戏画布，用户带键位显示
// 实测后明确要求模组 UI 也进拖尾。IMGUI（UMM 设置面板）不走相机，技术上
// 罩不到，属硬限制。
//
// 与纯净相机渲染的互斥：渲染期 RenderOverlayCapture 已把画布接管到捕获相机
// （CleanCamera 下 RT 本来就逐帧累积，UI 拖尾天然成立），此时本补丁**跳过**，
// 避免双重接管打架；「游戏画面」(WholeScreen) 来源拍的是后备缓冲，实机迁移
// 的结果直接进片，无需特判。
internal static class Patch_HallOfMirrorsUI
{
    private const string CamName = "SpectreReplay HOM UI Cam";

    private static Camera _homCam;
    private static readonly List<Canvas> _canvases = new List<Canvas>();
    private static readonly List<RenderMode> _oldModes = new List<RenderMode>();
    private static readonly List<Camera> _oldWorldCameras = new List<Camera>();
    private static bool _migrated;

    [SpectrePatch(typeof(scrController), "EnableHallOfMirrors", PatchType.Postfix, Feature = FeatureKeys.HallOfMirrorsUI)]
    internal static void Postfix(bool homEnabled)
    {
        if (homEnabled) Apply();
        else Restore();
    }

    private static void Apply()
    {
        if (_migrated) return;
        // 纯净相机渲染期：画布归 RenderOverlayCapture 管，这里绝不插手
        if (VideoRender.Active && VideoRender.CurrentSurface == VideoSurface.CleanCamera)
            return;
        // 迁移**当前所有** ScreenSpaceOverlay 根画布——游戏 UI 与模组 UI
        // （键位显示等）一起进镜厅。范围应实测裁定放宽：用户带键位显示
        // 测试后明确要求模组 UI 也进拖尾。IMGUI（UMM 面板）不走相机，
        // 技术上罩不到，属硬限制。
        Canvas[] overlays = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        List<Canvas> targets = new List<Canvas>();
        List<RenderMode> modes = new List<RenderMode>();
        List<Camera> cams = new List<Camera>();
        int layers = 0;
        foreach (Canvas c in overlays)
        {
            // 只取根画布（无父 Canvas）：嵌套画布跟随根的模式，单独迁会打架
            if (c.transform.parent != null && c.transform.parent.GetComponentInParent<Canvas>() != null)
                continue;
            if (c.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            targets.Add(c);
            modes.Add(c.renderMode);
            cams.Add(c.worldCamera);
            layers |= 1 << c.gameObject.layer;
        }
        if (targets.Count == 0) return;

        try
        {
            Camera main = scrController.instance != null && scrController.instance.camy != null
                ? scrController.instance.camy.camobj : null;
            _homCam = new GameObject(CamName).AddComponent<Camera>();
            // 不清屏 = 拖尾的整个机制所在。Depth 只清深度，颜色残留逐帧累积。
            _homCam.clearFlags = CameraClearFlags.Depth;
            // 遮罩 = 全部被迁画布层的并集
            _homCam.cullingMask = layers;
            _homCam.orthographic = true;
            _homCam.orthographicSize = 5f;
            _homCam.transform.position = Vector3.zero;
            _homCam.transform.rotation = Quaternion.identity;
            // 排在游戏相机之后渲染：相机按 depth 升序出画；主相机的 depth
            // 读不到时取大值兜底（Overlay 相机惯例是任意大的正 depth）。
            _homCam.depth = (main != null ? main.depth : 0f) + 100f;

            _canvases.Clear(); _oldModes.Clear(); _oldWorldCameras.Clear();
            for (int i = 0; i < targets.Count; i++)
            {
                _canvases.Add(targets[i]);
                _oldModes.Add(modes[i]);
                _oldWorldCameras.Add(cams[i]);
                // 先指相机再切模式：与 RenderOverlayCapture 同款顺序——切到
                // ScreenSpaceCamera 的瞬间若 worldCamera 为空，画布会闪到世界原点。
                targets[i].worldCamera = _homCam;
                targets[i].renderMode = RenderMode.ScreenSpaceCamera;
            }
            _migrated = true;
            Debug.Log("[SpectreReplay] 镜厅罩 UI 已接管 " + targets.Count + " 个画布（层并集 0x" + layers.ToString("X") + "）");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 镜厅罩 UI 迁移失败: " + ex.Message);
            Restore();
        }
    }

    private static void Restore()
    {
        if (!_migrated) return;
        _migrated = false;
        try
        {
            // 画布可能已随场景销毁（假 null）：Unity 语义 == 判空，活着才还原
            for (int i = 0; i < _canvases.Count; i++)
            {
                Canvas c = _canvases[i];
                if (c == null) continue;
                c.renderMode = _oldModes[i];
                c.worldCamera = _oldWorldCameras[i];
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 镜厅罩 UI 还原画布失败: " + ex.Message);
        }
        try
        {
            // 相机不挂 DontDestroyOnLoad：随场景销毁是兜底；这里活着一并清
            if (_homCam != null)
                UnityEngine.Object.Destroy(_homCam.gameObject);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[SpectreReplay] 镜厅罩 UI 清理相机失败: " + ex.Message);
        }
        _canvases.Clear();
        _oldModes.Clear();
        _oldWorldCameras.Clear();
        _homCam = null;
    }
}
