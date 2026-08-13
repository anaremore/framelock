using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FrameLock.Windows;

namespace FrameLock.TestHarness;

public partial class MainWindow : Window
{
    private const int WindowMessageGetMinMaxInfo = 0x0024;
    private readonly DispatcherTimer _metricsTimer;
    private nint _handle;
    private bool _resizeToggle;

    public MainWindow()
    {
        InitializeComponent();
        _metricsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, UpdateMetrics, Dispatcher);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProcedure);
    }

    private static nint WindowProcedure(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        _ = windowHandle;
        _ = wParam;
        if (message == WindowMessageGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaximumTrackSize.X = 20_000;
            info.MaximumTrackSize.Y = 20_000;
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
        }

        return 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _handle = new WindowInteropHelper(this).Handle;
        UpdateMetrics(this, EventArgs.Empty);
        ScheduleRequestedActions(Environment.GetCommandLineArgs().Skip(1));
    }

    private void UpdateMetrics(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_handle == 0)
        {
            return;
        }

        try
        {
            var metrics = WindowSizingService.GetMetrics(_handle);
            ClientSizeText.Text = metrics.Client.DisplayName;
            DpiText.Text = $"{metrics.ScalePercent}% scaling · {metrics.Dpi} dpi";
            HandleText.Text = $"HWND 0x{_handle.ToInt64():X}";
        }
        catch (WindowSizingException)
        {
            ClientSizeText.Text = "Window unavailable";
        }
    }

    private void OnResizeNow(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ResizeMyself();
    }

    private void ResizeMyself()
    {
        _resizeToggle = !_resizeToggle;
        Width = _resizeToggle ? 777 : 963;
        Height = _resizeToggle ? 555 : 681;
    }

    private void ScheduleRequestedActions(IEnumerable<string> arguments)
    {
        foreach (var argument in arguments)
        {
            if (TryReadDelay(argument, "--resize-once=", out var resizeDelay))
            {
                Schedule(resizeDelay, ResizeMyself);
            }
            else if (TryReadDelay(argument, "--minimize-after=", out var minimizeDelay))
            {
                Schedule(minimizeDelay, () => WindowState = WindowState.Minimized);
            }
            else if (TryReadDelay(argument, "--restore-after=", out var restoreDelay))
            {
                Schedule(restoreDelay, () => WindowState = WindowState.Normal);
            }
            else if (TryReadDelay(argument, "--close-after=", out var closeDelay))
            {
                Schedule(closeDelay, Close);
            }
        }
    }

    private void Schedule(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private static bool TryReadDelay(string argument, string prefix, out TimeSpan delay)
    {
        delay = default;
        return argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(argument[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) &&
               milliseconds >= 0 &&
               (delay = TimeSpan.FromMilliseconds(milliseconds)) >= TimeSpan.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved;
        internal NativePoint MaximumSize;
        internal NativePoint MaximumPosition;
        internal NativePoint MinimumTrackSize;
        internal NativePoint MaximumTrackSize;
    }
}
