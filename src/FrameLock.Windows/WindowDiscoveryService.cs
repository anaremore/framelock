using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameLock.Core;

namespace FrameLock.Windows;

public sealed class WindowDiscoveryService
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
        "MsgrIMEWindowClass",
    };

    public static IReadOnlyList<WindowTarget> GetWindows(uint excludedProcessId = 0)
    {
        var windows = new List<WindowTarget>();
        var shellWindow = NativeMethods.GetShellWindow();

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (TryCreateTarget(handle, shellWindow, excludedProcessId, out var target))
            {
                windows.Add(target);
            }

            return true;
        }, 0);

        return windows
            .OrderBy(window => window.ApplicationName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool TryCreateTarget(
        nint handle,
        nint shellWindow,
        uint excludedProcessId,
        out WindowTarget target)
    {
        target = null!;
        if (handle == 0 ||
            handle == shellWindow ||
            !NativeMethods.IsWindowVisible(handle) ||
            NativeMethods.GetAncestor(handle, NativeMethods.GaRoot) != handle ||
            !NativeMethods.GetWindowRect(handle, out var outer) ||
            outer.Width < 50 || outer.Height < 50)
        {
            return false;
        }

        var titleLength = NativeMethods.GetWindowTextLengthW(handle);
        if (titleLength <= 0)
        {
            return false;
        }

        var titleBuffer = new char[titleLength + 1];
        var copiedTitleLength = NativeMethods.GetWindowTextW(handle, titleBuffer, titleBuffer.Length);
        if (copiedTitleLength <= 0)
        {
            return false;
        }

        var title = new string(titleBuffer, 0, copiedTitleLength).Trim();
        if (title.Length == 0)
        {
            return false;
        }

        var classBuffer = new char[256];
        var copiedClassLength = NativeMethods.GetClassNameW(handle, classBuffer, classBuffer.Length);
        var className = copiedClassLength > 0 ? new string(classBuffer, 0, copiedClassLength) : string.Empty;
        if (ExcludedClasses.Contains(className))
        {
            return false;
        }

        if (NativeMethods.DwmGetWindowAttribute(
                handle,
                NativeMethods.DwmwaCloaked,
                out var cloaked,
                sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var isToolWindow = (extendedStyle & NativeMethods.WsExToolWindow) != 0;
        var isApplicationWindow = (extendedStyle & NativeMethods.WsExAppWindow) != 0;
        if ((isToolWindow || NativeMethods.GetWindow(handle, NativeMethods.GwOwner) != 0) && !isApplicationWindow)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0 || processId == excludedProcessId)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            var processName = process.ProcessName;
            var executablePath = TryGetExecutablePath(process);
            var applicationName = GetApplicationName(executablePath, processName);
            var startTimeTicks = TryGetStartTimeTicks(process);
            var preferenceKey = string.IsNullOrWhiteSpace(executablePath)
                ? processName.ToUpperInvariant()
                : executablePath.ToUpperInvariant();

            target = new WindowTarget(
                handle,
                processId,
                startTimeTicks,
                processName,
                applicationName,
                title,
                executablePath,
                preferenceKey);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static long TryGetStartTimeTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Win32Exception)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static string GetApplicationName(string? executablePath, string processName)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(executablePath);
                var description = version.FileDescription?.Trim();
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description;
                }

                var product = version.ProductName?.Trim();
                if (!string.IsNullOrWhiteSpace(product))
                {
                    return product;
                }
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or
                IOException or
                UnauthorizedAccessException or
                ArgumentException)
            {
            }
        }

        return processName;
    }
}
