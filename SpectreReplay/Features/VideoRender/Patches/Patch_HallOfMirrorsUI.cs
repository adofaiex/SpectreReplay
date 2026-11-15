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
// 游戏与模组的 UI 画布是 ScreenSpaceOverlay：不经过任何相机，直接画在最终
// 画面最上层，所以累积罩不到它们——镜厅开着时 UI 永远清晰浮在拖尾之上。
//
// 本补丁在镜厅开启时把**全部根画布**迁到一台**同样不清屏**的专属相机上，
// UI 每帧画在上一次的残留上——UI 与游戏世界一起进入镜厅。镜厅关闭或离开
// 关卡时逐画布完整还原。
//
// 三个实现要点（对照参照实现的同款行为语义，用更稳的手段达成）：
//   ① 无条件接管全部根画布：包括已经是 ScreenSpaceCamera 的模组画布——
//      只迁 Overlay 会漏掉它们（实测教训）；isRootCanvas 过滤嵌套画布
//      （嵌套画布没有自己的 renderMode，跟随根）。
//   ② 相机遮罩 = 每棵画布子树的层并集：UGUI 的剔除按**每个子节点的 layer**
//      逐个走，只看画布根的层会漏渲染子树里层不一致的元素（实测教训）。
//      收集并集而不是把子树改层——零改动、还原零成本。
//   ③ sortingOrder 抬升：迁入同一台相机后画布间按 sortingOrder 排序，
//      <10000 的统一 += 10000（保持相对前后关系，且压过游戏世界元素）。
//
// 与纯净相机渲染的互斥：渲染期 RenderOverlayCapture 已把画布接管到捕获相机
// （CleanCamera 下 RT 本来就逐帧累积，UI 拖尾天然成立），此时本补丁**跳过**，
// 避免双重接管打架；「游戏画面」(WholeScreen) 来源拍的是后备缓冲，实机迁移
// 的结果直接进片，无需特判。IMGUI（UMM 设置面板）不走相机，技术上罩不到。
internal static class Patch_HallOfMirrorsUI
{
    private const string CamName = "SpectreReplay HOM UI Cam";
    // 画布间排序抬升阈值：低于它的统一抬到它上面（参照实现同款数值语义），
    // 保证迁入相机后「游戏 UI 在上、各模组 UI 相对顺序不变」。
    private const int SortOrderBoost = 10000;

    private static Camera _homCam;
    private static readonly List<Canvas> _canvases = new List<Canvas>();
    private static readonly List<RenderMode> _oldModes = new List<RenderMode>();
    private static readonly List<Camera> _oldWorldCameras = new List<Camera>();
    private static readonly List<int> _oldOrders = new List<int>();
    private static bool _migrated;

    [SpectrePatch(typeof(scrController), "EnableHallOfMirrors", PatchType.Postfix, Feature = FeatureKeys.HallOfMirrorsUI)]
    internal static void Postfix(bool homEnabled)
    {
        if (homEnabled) Apply();
        else Restore();
    }

    private static void Apply()
    {
        // 纯净相机渲染期：画布归 RenderOverlayCapture 管，这里绝不插手
        if (VideoRender.Active && VideoRender.CurrentSurface == VideoSurface.CleanCamera)
            return;

        // 谱面 HOM 事件是逐段触发的（enabled=true 反复出现，中间可能穿插
        // false），EnableHallOfMirrors 会被反复调用——游戏侧幂等（反复设同值
        // 无害），本迁移器也必须幂等：已迁移时**只补迁新出现的画布**，绝不
        // 整体还原再重来（每次还原都会把已累积的拖尾断掉一帧，反复开关时
        // 观感就是「UI 时而有拖尾时而没有」）。

        // 收集全部**根**画布（含已是 ScreenSpaceCamera 的——只迁 Overlay 会
        // 漏掉模组画布，实测教训），同时累计每棵子树的层并集。
        Canvas[] all = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        List<Canvas> targets = new List<Canvas>();
        List<RenderMode> modes = new List<RenderMode>();
        List<Camera> cams = new List<Camera>();
        List<int> orders = new List<int>();
        int mask = 0;
        foreach (Canvas c in all)
        {
            if (!c.isRootCanvas) continue;
            // 已被本迁移器接管过的画布跳过：它们的快照已在册，重复入册会把
            // 「迁移后的状态」当原状存下来，还原就回不去了。
            if (_canvases.Contains(c))
            {
                Transform[] tree = c.transform.GetComponentsInChildren<Transform>(false);
                foreach (Transform t in tree)
                    mask |= 1 << t.gameObject.layer;
                continue;
            }
            targets.Add(c);
            modes.Add(c.renderMode);
            cams.Add(c.worldCamera);
            orders.Add(c.sortingOrder);
            Transform[] tree2 = c.transform.GetComponentsInChildren<Transform>(false);
            foreach (Transform t in tree2)
                mask |= 1 << t.gameObject.layer;
        }

        // 相机还没建且没有新目标：无事可做
        if (targets.Count == 0 && _homCam == null) return;

        try
        {
            if (_homCam == null)
            {
                Camera main = scrController.instance != null && scrController.instance.camy != null
                    ? scrController.instance.camy.camobj : null;
                _homCam = new GameObject(CamName).AddComponent<Camera>();
                // 不清屏 = 拖尾的整个机制所在。Depth 只清深度，颜色残留逐帧累积。
                _homCam.clearFlags = CameraClearFlags.Depth;
                // 遮罩在下方随新画布并入后统一更新（要点②）。
                _homCam.orthographic = true;
                _homCam.orthographicSize = 5f;
                _homCam.transform.position = Vector3.zero;
                _homCam.transform.rotation = Quaternion.identity;
                // 排在游戏相机之后渲染：相机按 depth 升序出画；主相机的 depth
                // 读不到时取大值兜底（Overlay 相机惯例是任意大的正 depth）。
                _homCam.depth = (main != null ? main.depth : 0f) + 100f;
            }

            int fresh = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                Canvas c = targets[i];
                _canvases.Add(c);
                _oldModes.Add(modes[i]);
                _oldWorldCameras.Add(cams[i]);
                _oldOrders.Add(orders[i]);
                fresh++;
                // 先指相机再切模式：与 RenderOverlayCapture 同款顺序——切到
                // ScreenSpaceCamera 的瞬间若 worldCamera 为空，画布会闪到世界原点。
                c.worldCamera = _homCam;
                c.renderMode = RenderMode.ScreenSpaceCamera;
                if (c.sortingOrder < SortOrderBoost) c.sortingOrder += SortOrderBoost;
            }
            // 已迁画布子树的层可能变化（模组动态改层），每次都把并集刷新进遮罩
            _homCam.cullingMask = mask;
            _migrated = _canvases.Count > 0;
            if (fresh > 0)
            {
                // 逐画布诊断：键位显示这类第三方画布接没接管、当时是什么模式，
                // 只有把名单打出来才能定位「没被投影」是没扫到还是没渲染。
                string names = string.Join(" | ", System.Linq.Enumerable.Select(targets,
                    (Func<Canvas, string>)(c => c.name + " (L" + c.gameObject.layer
                    + ", S" + c.sortingOrder + ")")));
                Debug.Log("[SpectreReplay] 镜厅罩 UI 新接管 " + fresh + " 个根画布（累计 "
                    + _canvases.Count + "）：" + names + "（层并集 0x" + mask.ToString("X") + "）");
            }
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
                c.sortingOrder = _oldOrders[i];
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
        _oldOrders.Clear();
        _homCam = null;
    }
}
