using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameLock.Core;

namespace FrameLock.Windows;

public sealed class WindowSizingException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed record WindowRestoreSnapshot(TargetStamp Target, NativeWindowPlacement Placement);

public static class WindowSizingService
{
    private const int MaximumCorrectionPasses = 5;

    public static WindowMetrics GetMetrics(WindowTarget target)
    {
        EnsureTarget(target);
        return GetMetrics(target.Handle);
    }

    public static WindowMetrics GetMetrics(nint windowHandle)
    {
        if (!NativeMethods.IsWindow(windowHandle))
        {
            throw new WindowSizingException("The selected window is no longer available.");
        }

        if (!NativeMethods.GetClientRect(windowHandle, out var client) ||
            !NativeMethods.GetWindowRect(windowHandle, out var outer))
        {
            throw CreateWin32Exception("Windows could not measure the selected window.");
        }

        var dpi = NativeMethods.GetDpiForWindow(windowHandle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        return new WindowMetrics(
            new Resolution(Math.Max(0, client.Width), Math.Max(0, client.Height)),
            new Resolution(Math.Max(0, outer.Width), Math.Max(0, outer.Height)),
            dpi,
            new PixelRect(outer.Left, outer.Top, outer.Right, outer.Bottom),
            NativeMethods.IsIconic(windowHandle),
            NativeMethods.IsZoomed(windowHandle));
    }

    public static SizingResult SetClientSize(WindowTarget target, Resolution requested)
    {
        if (!requested.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }

        EnsureTarget(target);
        if (NativeMethods.IsIconic(target.Handle))
        {
            throw new WindowSizingException("Restore the selected window before locking its size.");
        }

        if (NativeMethods.IsZoomed(target.Handle))
        {
            if (!NativeMethods.ShowWindowAsync(target.Handle, NativeMethods.SwRestore))
            {
                throw CreateWin32Exception("Windows could not restore the maximized target before sizing it.");
            }

            for (var attempt = 0; attempt < 20 && NativeMethods.IsZoomed(target.Handle); attempt++)
            {
                Thread.Sleep(25);
            }

            if (NativeMethods.IsZoomed(target.Handle))
            {
                throw new WindowSizingException("The selected application would not leave maximized mode. Restore it and try again.");
            }
        }

        var metrics = GetMetrics(target);
        var outerSize = metrics.Client.Width > 0 && metrics.Client.Height > 0
            ? WindowSizingMath.EstimateOuterSize(requested, metrics.Client, metrics.Outer)
            : EstimateOuterFromStyles(target.Handle, requested, metrics.Dpi);

        var correctionPasses = 0;
        for (; correctionPasses <= MaximumCorrectionPasses; correctionPasses++)
        {
            EnsureTarget(target);
            SetOuterSize(target.Handle, outerSize);
            Thread.Sleep(15);
            metrics = GetMetrics(target);

            if (metrics.Client == requested)
            {
                return new SizingResult(requested, metrics, correctionPasses);
            }

            outerSize = WindowSizingMath.CorrectOuterSize(requested, metrics.Client, metrics.Outer);
        }

        throw new WindowSizingException(
            $"{target.ApplicationName} would not accept an exact {requested.DisplayName} content area. " +
            $"Windows reported {metrics.Client.DisplayName} after resizing.");
    }

    public static WindowMetrics Center(WindowTarget target)
    {
        EnsureTarget(target);
        if (NativeMethods.IsIconic(target.Handle))
        {
            throw new WindowSizingException("Restore the selected window before centering it.");
        }

        var metrics = GetMetrics(target);
        var monitor = NativeMethods.MonitorFromWindow(target.Handle, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo))
        {
            throw CreateWin32Exception("Windows could not find the target window's monitor.");
        }

        var x = monitorInfo.WorkArea.Left + (monitorInfo.WorkArea.Width - metrics.Outer.Width) / 2;
        var y = monitorInfo.WorkArea.Top + (monitorInfo.WorkArea.Height - metrics.Outer.Height) / 2;
        if (!NativeMethods.SetWindowPos(
                target.Handle,
                0,
                x,
                y,
                0,
                0,
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoOwnerZOrder))
        {
            throw CreateWin32Exception("Windows could not center the selected window.");
        }

        return GetMetrics(target);
    }

    public static bool IsTargetAlive(WindowTarget target) =>
        TryGetTargetStamp(target.Handle, out var current) &&
        RestorePolicy.CanRestore(TargetStamp.From(target), current, isWindow: true);

    internal static WindowRestoreSnapshot CaptureRestoreSnapshot(WindowTarget target)
    {
        EnsureTarget(target);
        var placement = new NativeWindowPlacement { Length = Marshal.SizeOf<NativeWindowPlacement>() };
        if (!NativeMethods.GetWindowPlacement(target.Handle, ref placement))
        {
            throw CreateWin32Exception("Windows could not capture the target window's original placement.");
        }

        return new WindowRestoreSnapshot(TargetStamp.From(target), placement);
    }

    internal static void Restore(WindowTarget target, WindowRestoreSnapshot snapshot)
    {
        if (!TryGetTargetStamp(target.Handle, out var current) ||
            !RestorePolicy.CanRestore(snapshot.Target, current, isWindow: true))
        {
            return;
        }

        if (!NativeMethods.SetWindowPlacement(target.Handle, snapshot.Placement))
        {
            throw CreateWin32Exception("Windows could not restore the target window's original size.");
        }
    }

    internal static bool TryGetTargetStamp(nint handle, out TargetStamp stamp)
    {
        stamp = default;
        if (!NativeMethods.IsWindow(handle))
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        long startTimeTicks;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            startTimeTicks = process.StartTime.ToUniversalTime().Ticks;
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
            startTimeTicks = 0;
        }

        stamp = new TargetStamp(handle, processId, startTimeTicks);
        return true;
    }

    private static void SetOuterSize(nint windowHandle, Resolution outerSize)
    {
        if (!NativeMethods.SetWindowPos(
                windowHandle,
                0,
                0,
                0,
                outerSize.Width,
                outerSize.Height,
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoOwnerZOrder))
        {
            throw CreateWin32Exception(
                "Windows refused to resize this application. If it runs as administrator, run FrameLock at the same level.");
        }
    }

    private static Resolution EstimateOuterFromStyles(nint handle, Resolution requested, uint dpi)
    {
        var rectangle = new NativeRect { Right = requested.Width, Bottom = requested.Height };
        var style = unchecked((uint)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlStyle).ToInt64());
        var extendedStyle = unchecked((uint)NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64());
        if (!NativeMethods.AdjustWindowRectExForDpi(
                ref rectangle,
                style,
                NativeMethods.GetMenu(handle) != 0,
                extendedStyle,
                dpi))
        {
            throw CreateWin32Exception("Windows could not calculate the target window's frame size.");
        }

        return new Resolution(rectangle.Width, rectangle.Height);
    }

    private static void EnsureTarget(WindowTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!TryGetTargetStamp(target.Handle, out var current) ||
            !RestorePolicy.CanRestore(TargetStamp.From(target), current, isWindow: true))
        {
            throw new WindowSizingException("The selected window is no longer available.");
        }
    }

    private static WindowSizingException CreateWin32Exception(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return error == 0
            ? new WindowSizingException(message)
            : new WindowSizingException(message, new Win32Exception(error));
    }
}
