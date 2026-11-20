using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SpectreReplay.Features.EffectRemover;

// Windows 文件身份：卷序列号 + 128 位文件 ID。打开最终目标而不是重解析点本身，
// 因而硬链接、符号链接、目录联接和扩展路径都按底层文件比较。
[StructLayout(LayoutKind.Sequential)]
internal struct LevelFileIdentity : IEquatable<LevelFileIdentity>
{
    private ulong volumeSerial;
    private ulong fileIdLow;
    private ulong fileIdHigh;

    public bool Equals(LevelFileIdentity other)
        => volumeSerial == other.volumeSerial && fileIdLow == other.fileIdLow && fileIdHigh == other.fileIdHigh;

    public override bool Equals(object obj) => obj is LevelFileIdentity other && Equals(other);

    public override int GetHashCode() => volumeSerial.GetHashCode() ^ fileIdLow.GetHashCode() ^ fileIdHigh.GetHashCode();

    // true + null 只表示确认不存在；其他探测错误不可当作不存在放行。
    internal static bool TryRead(string path, out LevelFileIdentity? identity)
    {
        identity = null;
        if (Path.DirectorySeparatorChar != '\\')
        {
            // 无底层文件 ID 的平台仍可另存新文件，但不能判断已有文件是否为别名。
            try { File.GetAttributes(path); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            return false;
        }

        const uint openExisting = 3;
        const uint shareReadWriteDelete = 7;
        const int fileIdInfo = 18;
        // 零访问权限只查询元数据，不需要读取文件内容，也不创建或锁住目标文件。
        using var handle = CreateFileW(path, 0, shareReadWriteDelete, IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        int openError = Marshal.GetLastWin32Error();
        if (handle.IsInvalid) return openError == 2 || openError == 3; // FILE_NOT_FOUND / PATH_NOT_FOUND
        if (!GetFileInformationByHandleEx(handle, fileIdInfo, out LevelFileIdentity info, 24)) return false;
        identity = info;
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
        out LevelFileIdentity info, uint size);
}
