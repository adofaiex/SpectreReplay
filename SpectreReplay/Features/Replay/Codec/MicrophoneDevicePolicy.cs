using System;

namespace SpectreReplay;

/// <summary>从配置值和一次性设备快照解析可用麦克风；无设备时明确返回 null。</summary>
internal static class MicrophoneDevicePolicy
{
    internal static string Resolve(string preferred, string[] devices, out bool usedFallback)
    {
        usedFallback = false;
        if (devices == null || devices.Length == 0)
            return null;
        if (!string.IsNullOrEmpty(preferred)
            && !string.Equals(preferred, "None", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (string.Equals(preferred, devices[i], StringComparison.Ordinal))
                    return devices[i];
            }
        }
        for (int i = 0; i < devices.Length; i++)
        {
            if (!string.IsNullOrEmpty(devices[i]))
            {
                usedFallback = true;
                return devices[i];
            }
        }
        return null;
    }
}
