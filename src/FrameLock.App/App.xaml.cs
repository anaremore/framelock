using System.Windows;

namespace FrameLock.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeService.ApplySystemTheme(Resources);
        base.OnStartup(e);
    }
}
