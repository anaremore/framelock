using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Threading;
using FrameLock.Windows;

namespace FrameLock.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainViewModel _viewModel = new();
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        NativeWindowAppearance.SetDarkTitleBar(
            new WindowInteropHelper(this).Handle,
            ThemeService.UsesDarkMode);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName != nameof(MainViewModel.ErrorMessage) || !_viewModel.HasError)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            var peer = UIElementAutomationPeer.FromElement(ErrorBorder) ??
                       UIElementAutomationPeer.CreatePeerForElement(ErrorBorder);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _viewModel.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
