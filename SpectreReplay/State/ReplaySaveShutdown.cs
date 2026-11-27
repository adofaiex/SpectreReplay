using SpectreReplay.Features.Replay;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SpectreReplay;

// quitting 已晚于游戏的 SteamClient.Shutdown；必须在 wantsToQuit 阶段留住正常帧循环，
// 让等待录音封头的回调、后台写盘和主线程完成通知都能结束，再交给游戏正常退出。
internal static class ReplaySaveShutdown
{
    private static bool quitDeferred;
    private static int quitGeneration;

    internal static bool WantsToQuit()
    {
        if (!ReplaySaveOperation.HasPendingSaves)
        {
            // 保存刚结束时玩家可能先主动重试了退出；取消旧自动重试，避免其它
            // 处理器否决/取消确认后，下一帧又被等待器重复弹出退出请求。
            quitDeferred = false;
            return true;
        }
        if (!quitDeferred)
        {
            var go = new GameObject("SpectreReplay.SaveBeforeQuit");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<PendingQuit>().Generation = ++quitGeneration;
            quitDeferred = true;
            Debug.Log("[SpectreReplay] 正在完成已接受的回放保存，完成后退出游戏");
        }
        return false;
    }

    private sealed class PendingQuit : MonoBehaviour
    {
        internal int Generation;

        private void Update()
        {
            if (!quitDeferred || Generation != quitGeneration)
            {
                Object.Destroy(gameObject);
                return;
            }
            if (ReplaySaveOperation.HasPendingSaves) return;
            quitDeferred = false;
            Object.Destroy(gameObject);
            // 重新经过 wantsToQuit，保留游戏和其它模组的退出否决权，不强制终止进程。
            Application.Quit();
        }
    }
}
