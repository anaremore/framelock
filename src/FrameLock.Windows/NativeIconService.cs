using System.Runtime.InteropServices;

namespace FrameLock.Windows;

public static class NativeIconService
{
    public static nint AcquireIcon(nint windowHandle, string? executablePath)
    {
        foreach (var classIndex in new[] { NativeMethods.GclpHiconsm, NativeMethods.GclpHicon })
        {
            var classIcon = NativeMethods.GetClassLongPtr(windowHandle, classIndex);
            if (classIcon != 0)
            {
                var copy = NativeMethods.CopyIcon(classIcon);
                if (copy != 0)
                {
                    return copy;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            var result = NativeMethods.SHGetFileInfoW(
                executablePath,
                0,
                out var fileInfo,
                checked((uint)Marshal.SizeOf<ShellFileInfo>()),
                NativeMethods.ShgfiIcon | NativeMethods.ShgfiSmallIcon);
            if (result != 0 && fileInfo.Icon != 0)
            {
                return fileInfo.Icon;
            }
        }

        if (NativeMethods.SendMessageTimeoutW(
                windowHandle,
                NativeMethods.WmGetIcon,
                2,
                0,
                NativeMethods.SmtoAbortIfHung,
                30,
                out var windowIcon) != 0 && windowIcon != 0)
        {
            var copy = NativeMethods.CopyIcon(windowIcon);
            if (copy != 0)
            {
                return copy;
            }
        }

        return 0;
    }

    public static void ReleaseIcon(nint icon)
    {
        if (icon != 0)
        {
            _ = NativeMethods.DestroyIcon(icon);
        }
    }
}
