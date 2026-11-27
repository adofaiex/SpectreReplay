using System;

namespace UiPreview;

internal static class SaveToastOnceCheck
{
    internal static int Run(string projectRoot)
    {
        Console.WriteLine("  -- save toast once (real save/completion/hotkey) --");
        // 与音频边界检查共用真实源码编译器，但单独执行通知/热键场景，避免字符串锚点假绿。
        return BoundaryRegressionCheck.RunNotifications(projectRoot);
    }
}
