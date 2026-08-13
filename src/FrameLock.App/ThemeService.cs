using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FrameLock.App;

internal static class ThemeService
{
    internal static bool UsesDarkMode { get; private set; }

    internal static void ApplySystemTheme(ResourceDictionary resources)
    {
        if (SystemParameters.HighContrast)
        {
            UsesDarkMode = false;
            resources["AppBackgroundBrush"] = SystemColors.WindowBrush;
            resources["SurfaceBrush"] = SystemColors.ControlBrush;
            resources["SurfaceAltBrush"] = SystemColors.ControlLightBrush;
            resources["TextPrimaryBrush"] = SystemColors.WindowTextBrush;
            resources["TextSecondaryBrush"] = SystemColors.GrayTextBrush;
            resources["BorderBrush"] = SystemColors.ActiveBorderBrush;
            resources["ControlBackgroundBrush"] = SystemColors.WindowBrush;
            resources["ControlHoverBrush"] = SystemColors.ControlLightBrush;
            resources["ComboBackgroundBrush"] = SystemColors.WindowBrush;
            resources["ComboTextBrush"] = SystemColors.WindowTextBrush;
            resources["ComboSecondaryTextBrush"] = SystemColors.GrayTextBrush;
            resources["AccentBrush"] = SystemColors.HighlightBrush;
            resources["SelectionBrush"] = SystemColors.HighlightBrush;
            resources["SuccessBrush"] = SystemColors.HighlightBrush;
            resources["ErrorBrush"] = SystemColors.HotTrackBrush;
            resources["ErrorBackgroundBrush"] = SystemColors.ControlBrush;
            return;
        }

        var isDark = IsDarkMode();
        UsesDarkMode = isDark;
        resources["AppBackgroundBrush"] = Brush(isDark ? "#18191B" : "#F5F6F8");
        resources["SurfaceBrush"] = Brush(isDark ? "#232529" : "#FFFFFF");
        resources["SurfaceAltBrush"] = Brush(isDark ? "#2B2E33" : "#F0F2F5");
        resources["TextPrimaryBrush"] = Brush(isDark ? "#F5F6F7" : "#17202A");
        resources["TextSecondaryBrush"] = Brush(isDark ? "#B8BEC7" : "#56616F");
        resources["BorderBrush"] = Brush(isDark ? "#454950" : "#D5DAE1");
        resources["ControlBackgroundBrush"] = Brush(isDark ? "#2B2E33" : "#FFFFFF");
        resources["ControlHoverBrush"] = Brush(isDark ? "#3A3E45" : "#E9EDF2");
        resources["ComboBackgroundBrush"] = Brush("#FFFFFF");
        resources["ComboTextBrush"] = Brush("#17202A");
        resources["ComboSecondaryTextBrush"] = Brush("#56616F");
        resources["AccentBrush"] = Brush(isDark ? "#286BAA" : "#0F6CBD");
        resources["SelectionBrush"] = Brush(isDark ? "#396C99" : "#9CC8F1");
        resources["SuccessBrush"] = Brush(isDark ? "#65C98B" : "#187A3C");
        resources["ErrorBrush"] = Brush(isDark ? "#FF8B8B" : "#B42318");
        resources["ErrorBackgroundBrush"] = Brush(isDark ? "#3A2426" : "#FFF0EE");
    }

    private static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
