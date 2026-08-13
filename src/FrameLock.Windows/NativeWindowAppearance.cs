namespace FrameLock.Windows;

public static class NativeWindowAppearance
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    public static void SetDarkTitleBar(nint windowHandle, bool enabled)
    {
        if (windowHandle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var value = enabled ? 1 : 0;
        var result = NativeMethods.DwmSetWindowAttribute(
            windowHandle,
            UseImmersiveDarkMode,
            ref value,
            sizeof(int));
        if (result != 0)
        {
            _ = NativeMethods.DwmSetWindowAttribute(
                windowHandle,
                UseImmersiveDarkModeBefore20H1,
                ref value,
                sizeof(int));
        }
    }
}
