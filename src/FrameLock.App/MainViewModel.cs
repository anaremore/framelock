using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FrameLock.Core;
using FrameLock.Windows;

namespace FrameLock.App;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly JsonPreferencesStore _preferencesStore;
    private readonly FrameLockPreferences _preferences;
    private readonly WindowLockService _lockService = new();
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _lockCommand;
    private readonly AsyncRelayCommand _unlockCommand;
    private readonly AsyncRelayCommand _centerCommand;
    private readonly RelayCommand _usePresetCommand;
    private WindowChoice? _selectedWindow;
    private WindowTarget? _lockedTarget;
    private string _widthText;
    private string _heightText;
    private bool _isBusy;
    private string? _errorMessage;
    private WindowMetrics? _metrics;
    private bool _disposed;

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        var settingsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FrameLock",
            "settings.json");
        _preferencesStore = new JsonPreferencesStore(settingsPath);
        _preferences = _preferencesStore.Load();
        _widthText = _preferences.LastResolution.Width.ToString(CultureInfo.CurrentCulture);
        _heightText = _preferences.LastResolution.Height.ToString(CultureInfo.CurrentCulture);

        _refreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && !IsLocked);
        _lockCommand = new AsyncRelayCommand(LockAsync, () => !IsBusy && !IsLocked && SelectedWindow is not null);
        _unlockCommand = new AsyncRelayCommand(UnlockAsync, () => !IsBusy && IsLocked);
        _centerCommand = new AsyncRelayCommand(CenterAsync, () => !IsBusy && (IsLocked || SelectedWindow is not null));
        _usePresetCommand = new RelayCommand(UsePreset, _ => !IsBusy && !IsLocked);

        _lockService.StateChanged += OnLockStateChanged;
        _lockService.MetricsChanged += OnMetricsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WindowChoice> Windows { get; } = [];

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand LockCommand => _lockCommand;

    public ICommand UnlockCommand => _unlockCommand;

    public ICommand CenterCommand => _centerCommand;

    public ICommand UsePresetCommand => _usePresetCommand;

    public WindowChoice? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (!SetProperty(ref _selectedWindow, value) || value is null || IsLocked)
            {
                RaiseCommandStates();
                return;
            }

            SetResolution(_preferences.ResolutionFor(value.Target.PreferenceKey));
            _ = RefreshMetricsAsync(value.Target);
            RaiseCommandStates();
        }
    }

    public string WidthText
    {
        get => _widthText;
        set => SetProperty(ref _widthText, value);
    }

    public string HeightText
    {
        get => _heightText;
        set => SetProperty(ref _heightText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsLocked => _lockService.State.IsActive;

    public bool HasWindows => Windows.Count > 0;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string LockedStatus =>
        _lockService.State.Status == LockStatus.Suspended ? "Paused while minimized" : "Locked";

    public string LockedApplicationName => _lockedTarget?.ApplicationName ?? "Application";

    public string LockedTitle => _lockedTarget?.Title ?? string.Empty;

    public string LockedResolution =>
        _lockService.Resolution.IsValid ? _lockService.Resolution.DisplayName : string.Empty;

    public string CurrentContent => _metrics is null ? "—" : $"{_metrics.Client.DisplayName} content";

    public string CurrentWindow => _metrics is null ? "—" : $"{_metrics.Outer.DisplayName} window";

    public string CurrentDpi => _metrics is null ? "—" : $"{_metrics.ScalePercent}% DPI ({_metrics.Dpi} dpi)";

    public async Task InitializeAsync() => await RefreshAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lockService.StateChanged -= OnLockStateChanged;
        _lockService.MetricsChanged -= OnMetricsChanged;
        _lockService.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var previousHandle = SelectedWindow?.Target.Handle;
            var processId = checked((uint)Environment.ProcessId);
            var choices = await Task.Run(() =>
                WindowDiscoveryService.GetWindows(processId)
                    .Select(target => new WindowChoice(target))
                    .ToArray());

            Windows.Clear();
            foreach (var choice in choices)
            {
                Windows.Add(choice);
            }

            OnPropertyChanged(nameof(HasWindows));
            SelectedWindow = Windows.FirstOrDefault(choice => choice.Target.Handle == previousHandle) ??
                             Windows.FirstOrDefault();
            if (Windows.Count == 0)
            {
                _metrics = null;
                RaiseMetricsChanged();
            }

            _ = LoadIconsAsync(choices);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ErrorMessage = "FrameLock could not scan running applications. Try Refresh again.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadIconsAsync(IEnumerable<WindowChoice> choices)
    {
        foreach (var choice in choices)
        {
            var icon = await Task.Run(() => WindowChoice.LoadIcon(choice.Target));
            if (Windows.Contains(choice))
            {
                choice.SetIcon(icon);
            }
        }
    }

    private async Task LockAsync()
    {
        if (SelectedWindow is null)
        {
            ErrorMessage = "Choose an application to lock.";
            return;
        }

        if (!Resolution.TryParse(WidthText, HeightText, out var resolution, out var error))
        {
            ErrorMessage = error;
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        _lockedTarget = SelectedWindow.Target;
        RaiseLockedProperties();
        try
        {
            var result = await _lockService.LockAsync(_lockedTarget, resolution);
            _metrics = result.Metrics;
            RaiseMetricsChanged();
            _preferences.Remember(_lockedTarget.PreferenceKey, resolution);
            try
            {
                _preferencesStore.Save(_preferences);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = "The window is locked, but FrameLock could not save this preference.";
            }
        }
        catch (WindowSizingException exception)
        {
            _lockedTarget = null;
            ErrorMessage = exception.Message;
        }
        catch (OperationCanceledException)
        {
            _lockedTarget = null;
            ErrorMessage = "Locking was canceled.";
        }
        finally
        {
            IsBusy = false;
            RaiseLockedProperties();
        }
    }

    private async Task UnlockAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _lockService.UnlockAsync(restore: true);
        }
        catch (WindowSizingException exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            _lockedTarget = null;
            IsBusy = false;
            RaiseLockedProperties();
            await RefreshAsync();
        }
    }

    private async Task CenterAsync()
    {
        var target = IsLocked ? _lockedTarget : SelectedWindow?.Target;
        if (target is null)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            _metrics = await Task.Run(() => WindowSizingService.Center(target));
            RaiseMetricsChanged();
        }
        catch (WindowSizingException exception)
        {
            ErrorMessage = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshMetricsAsync(WindowTarget target)
    {
        try
        {
            var metrics = await Task.Run(() => WindowSizingService.GetMetrics(target));
            if (SelectedWindow?.Target.Handle == target.Handle || _lockedTarget?.Handle == target.Handle)
            {
                _metrics = metrics;
                RaiseMetricsChanged();
            }
        }
        catch (WindowSizingException)
        {
            if (!IsLocked)
            {
                ErrorMessage = "That application closed. Choose another window or Refresh the list.";
            }
        }
    }

    private void UsePreset(object? parameter)
    {
        if (parameter is not string text)
        {
            return;
        }

        var parts = text.Split(',');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], CultureInfo.InvariantCulture, out var width) &&
            int.TryParse(parts[1], CultureInfo.InvariantCulture, out var height))
        {
            SetResolution(new Resolution(width, height));
            ErrorMessage = null;
        }
    }

    private void SetResolution(Resolution resolution)
    {
        WidthText = resolution.Width.ToString(CultureInfo.CurrentCulture);
        HeightText = resolution.Height.ToString(CultureInfo.CurrentCulture);
    }

    private void OnLockStateChanged(object? sender, LockStateChangedEventArgs args)
    {
        _ = sender;
        _dispatcher.BeginInvoke(() =>
        {
            RaiseLockedProperties();
            if (args.State.Status is LockStatus.TargetClosed or LockStatus.Faulted)
            {
                ErrorMessage = args.State.Message;
                _lockedTarget = null;
                RaiseLockedProperties();
                _ = RefreshAsync();
            }
        });
    }

    private void OnMetricsChanged(object? sender, WindowMetricsChangedEventArgs args)
    {
        _ = sender;
        _dispatcher.BeginInvoke(() =>
        {
            _metrics = args.Metrics;
            RaiseMetricsChanged();
        });
    }

    private void RaiseLockedProperties()
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(LockedStatus));
        OnPropertyChanged(nameof(LockedApplicationName));
        OnPropertyChanged(nameof(LockedTitle));
        OnPropertyChanged(nameof(LockedResolution));
        RaiseCommandStates();
    }

    private void RaiseMetricsChanged()
    {
        OnPropertyChanged(nameof(CurrentContent));
        OnPropertyChanged(nameof(CurrentWindow));
        OnPropertyChanged(nameof(CurrentDpi));
    }

    private void RaiseCommandStates()
    {
        _refreshCommand.NotifyCanExecuteChanged();
        _lockCommand.NotifyCanExecuteChanged();
        _unlockCommand.NotifyCanExecuteChanged();
        _centerCommand.NotifyCanExecuteChanged();
        _usePresetCommand.NotifyCanExecuteChanged();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
