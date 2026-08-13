namespace FrameLock.Core;

public sealed record WindowTarget(
    nint Handle,
    uint ProcessId,
    long ProcessStartTimeUtcTicks,
    string ProcessName,
    string ApplicationName,
    string Title,
    string? ExecutablePath,
    string PreferenceKey)
{
    public string AccessibleName =>
        string.Equals(ApplicationName, Title, StringComparison.OrdinalIgnoreCase)
            ? ApplicationName
            : $"{ApplicationName}, {Title}";
}

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

public sealed record WindowMetrics(
    Resolution Client,
    Resolution Outer,
    uint Dpi,
    PixelRect OuterRect,
    bool IsMinimized,
    bool IsMaximized)
{
    public int ScalePercent => (int)Math.Round(Dpi / 96d * 100d);
}

public sealed record SizingResult(
    Resolution Requested,
    WindowMetrics Metrics,
    int CorrectionPasses)
{
    public bool IsExact => Metrics.Client == Requested;
}

public readonly record struct TargetStamp(
    nint Handle,
    uint ProcessId,
    long ProcessStartTimeUtcTicks)
{
    public static TargetStamp From(WindowTarget target) =>
        new(target.Handle, target.ProcessId, target.ProcessStartTimeUtcTicks);
}

public static class RestorePolicy
{
    public static bool CanRestore(TargetStamp captured, TargetStamp current, bool isWindow) =>
        isWindow &&
        captured.Handle != 0 &&
        captured.Handle == current.Handle &&
        captured.ProcessId == current.ProcessId &&
        captured.ProcessStartTimeUtcTicks == current.ProcessStartTimeUtcTicks;
}
