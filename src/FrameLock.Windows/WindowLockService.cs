using FrameLock.Core;
using System.Runtime.InteropServices;

namespace FrameLock.Windows;

public sealed class LockStateChangedEventArgs(LockLifecycleState state) : EventArgs
{
    public LockLifecycleState State { get; } = state;
}

public sealed class WindowMetricsChangedEventArgs(WindowMetrics metrics) : EventArgs
{
    public WindowMetrics Metrics { get; } = metrics;
}

public sealed class WindowLockService : IDisposable
{
    private readonly object _sync = new();
    private readonly NativeMethods.WinEventProc _winEventCallback;
    private GCHandle _winEventCallbackHandle;
    private Timer? _correctionTimer;
    private Timer? _watchdogTimer;
    private nint _objectHook;
    private nint _systemHook;
    private SynchronizationContext? _hookContext;
    private WindowTarget? _target;
    private WindowRestoreSnapshot? _restoreSnapshot;
    private Resolution _resolution;
    private int _enforcing;
    private TaskCompletionSource _enforcementIdle = CompletedSource();
    private bool _disposed;

    public event EventHandler<LockStateChangedEventArgs>? StateChanged;

    public event EventHandler<WindowMetricsChangedEventArgs>? MetricsChanged;

    public LockLifecycleState State { get; private set; } = LockLifecycleState.Initial;

    public WindowTarget? Target => _target;

    public Resolution Resolution => _resolution;

    public WindowLockService()
    {
        _winEventCallback = OnWinEvent;
        _winEventCallbackHandle = GCHandle.Alloc(_winEventCallback);
    }

    public async Task<SizingResult> LockAsync(
        WindowTarget target,
        Resolution resolution,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        if (!resolution.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        lock (_sync)
        {
            if (State.IsActive)
            {
                throw new InvalidOperationException("A window is already locked.");
            }

            Transition(LockSignal.BeginLock);
        }

        WindowRestoreSnapshot? snapshot = null;
        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = WindowSizingService.CaptureRestoreSnapshot(target);
                var sizingResult = WindowSizingService.SetClientSize(target, resolution);
                cancellationToken.ThrowIfCancellationRequested();
                return sizingResult;
            }, cancellationToken).ConfigureAwait(true);

            lock (_sync)
            {
                _target = target;
                _resolution = resolution;
                _restoreSnapshot = snapshot;
                _correctionTimer = new Timer(_ => _ = EnforceAsync(), null, Timeout.Infinite, Timeout.Infinite);
                _watchdogTimer = new Timer(_ => ScheduleEnforcement(), null, 1_200, 1_200);
                _hookContext = SynchronizationContext.Current;
                if (_hookContext is not null)
                {
                    InstallHooks(target.ProcessId);
                }
                Transition(LockSignal.SizeVerified);
            }

            MetricsChanged?.Invoke(this, new WindowMetricsChangedEventArgs(result.Metrics));
            return result;
        }
        catch
        {
            if (snapshot is not null)
            {
                try
                {
                    WindowSizingService.Restore(target, snapshot);
                }
                catch (WindowSizingException)
                {
                }
            }

            lock (_sync)
            {
                _ = StopCore();
                Transition(LockSignal.Unlock);
            }

            throw;
        }
    }

    public async Task UnlockAsync(bool restore = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowTarget? target;
        WindowRestoreSnapshot? snapshot;

        lock (_sync)
        {
            target = _target;
            snapshot = _restoreSnapshot;
            _ = StopCore();
            Transition(LockSignal.Unlock);
        }

        await WaitForEnforcementAsync().ConfigureAwait(true);

        if (restore && target is not null && snapshot is not null)
        {
            await Task.Run(() => WindowSizingService.Restore(target, snapshot)).ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        WindowTarget? target;
        WindowRestoreSnapshot? snapshot;
        bool hooksRemovedSynchronously;
        lock (_sync)
        {
            _disposed = true;
            target = _target;
            snapshot = _restoreSnapshot;
            hooksRemovedSynchronously = StopCore();
            State = LockLifecycleState.Initial;
            if (hooksRemovedSynchronously && _winEventCallbackHandle.IsAllocated)
            {
                _winEventCallbackHandle.Free();
            }
        }

        var enforcementFinished = SpinWait.SpinUntil(
            () => Volatile.Read(ref _enforcing) == 0,
            TimeSpan.FromSeconds(2));

        if (enforcementFinished && target is not null && snapshot is not null)
        {
            try
            {
                WindowSizingService.Restore(target, snapshot);
            }
            catch (WindowSizingException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    private void InstallHooks(uint processId)
    {
        _objectHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventObjectDestroy,
            NativeMethods.EventObjectLocationChange,
            0,
            _winEventCallback,
            processId,
            0,
            NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);
        _systemHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemMinimizeStart,
            NativeMethods.EventSystemMinimizeEnd,
            0,
            _winEventCallback,
            processId,
            0,
            NativeMethods.WineventOutOfContext | NativeMethods.WineventSkipOwnProcess);
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        _ = hook;
        _ = childId;
        _ = eventThread;
        _ = eventTime;

        var target = _target;
        if (target is null || windowHandle != target.Handle)
        {
            return;
        }

        if (eventType == NativeMethods.EventObjectDestroy && objectId == NativeMethods.ObjidWindow)
        {
            DeactivateForClosedTarget();
            return;
        }

        if (eventType == NativeMethods.EventSystemMinimizeStart)
        {
            lock (_sync)
            {
                if (State.Status == LockStatus.Locked)
                {
                    Transition(LockSignal.TargetMinimized);
                }
            }

            return;
        }

        if (eventType is NativeMethods.EventObjectLocationChange or
            NativeMethods.EventObjectShow or
            NativeMethods.EventSystemMinimizeEnd)
        {
            ScheduleEnforcement();
        }
    }

    private void ScheduleEnforcement()
    {
        lock (_sync)
        {
            if (!State.IsActive || _correctionTimer is null)
            {
                return;
            }

            _correctionTimer.Change(75, Timeout.Infinite);
        }
    }

    private async Task EnforceAsync()
    {
        if (Interlocked.Exchange(ref _enforcing, 1) != 0)
        {
            return;
        }

        var enforcementIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _enforcementIdle, enforcementIdle);

        try
        {
            WindowTarget? target;
            Resolution resolution;
            lock (_sync)
            {
                target = _target;
                resolution = _resolution;
            }

            if (target is null)
            {
                return;
            }

            if (!WindowSizingService.IsTargetAlive(target))
            {
                DeactivateForClosedTarget();
                return;
            }

            var metrics = await Task.Run(() => WindowSizingService.GetMetrics(target)).ConfigureAwait(false);
            if (metrics.IsMinimized)
            {
                lock (_sync)
                {
                    if (State.Status == LockStatus.Locked)
                    {
                        Transition(LockSignal.TargetMinimized);
                    }
                }

                return;
            }

            if (metrics.Client != resolution)
            {
                var result = await Task.Run(() => WindowSizingService.SetClientSize(target, resolution)).ConfigureAwait(false);
                metrics = result.Metrics;
            }

            lock (_sync)
            {
                if (_disposed || !State.IsActive || _target?.Handle != target.Handle)
                {
                    return;
                }

                if (State.Status == LockStatus.Suspended)
                {
                    Transition(LockSignal.TargetRestored);
                }
            }

            MetricsChanged?.Invoke(this, new WindowMetricsChangedEventArgs(metrics));
        }
        catch (WindowSizingException exception)
        {
            if (_target is null || !WindowSizingService.IsTargetAlive(_target))
            {
                DeactivateForClosedTarget();
            }
            else
            {
                lock (_sync)
                {
                    _ = StopCore();
                    Transition(LockSignal.Fail, exception.Message);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _enforcing, 0);
            enforcementIdle.TrySetResult();
        }
    }

    private void DeactivateForClosedTarget()
    {
        lock (_sync)
        {
            if (!State.IsActive)
            {
                return;
            }

            _ = StopCore();
            Transition(LockSignal.TargetDestroyed);
        }
    }

    private bool StopCore()
    {
        _correctionTimer?.Dispose();
        _correctionTimer = null;
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;

        var hooksRemovedSynchronously = RemoveHooksOnOwnerThread();

        _target = null;
        _restoreSnapshot = null;
        _resolution = default;
        return hooksRemovedSynchronously;
    }

    private bool RemoveHooksOnOwnerThread()
    {
        var objectHook = _objectHook;
        var systemHook = _systemHook;
        var hookContext = _hookContext;
        _objectHook = 0;
        _systemHook = 0;
        _hookContext = null;

        if (objectHook == 0 && systemHook == 0)
        {
            return true;
        }

        void RemoveHooks()
        {
            if (objectHook != 0)
            {
                _ = NativeMethods.UnhookWinEvent(objectHook);
            }

            if (systemHook != 0)
            {
                _ = NativeMethods.UnhookWinEvent(systemHook);
            }
        }

        if (hookContext is null || ReferenceEquals(SynchronizationContext.Current, hookContext))
        {
            RemoveHooks();
            return true;
        }

        hookContext.Post(_ =>
        {
            RemoveHooks();
            lock (_sync)
            {
                if (_disposed && _winEventCallbackHandle.IsAllocated)
                {
                    _winEventCallbackHandle.Free();
                }
            }
        }, null);
        return false;
    }

    private void Transition(LockSignal signal, string? message = null)
    {
        State = LockLifecycle.Reduce(State, signal, message);
        StateChanged?.Invoke(this, new LockStateChangedEventArgs(State));
    }

    private async Task WaitForEnforcementAsync()
    {
        while (Volatile.Read(ref _enforcing) != 0)
        {
            var idle = Volatile.Read(ref _enforcementIdle);
            if (idle.Task.IsCompleted)
            {
                await Task.Yield();
            }
            else
            {
                await idle.Task.ConfigureAwait(true);
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

}
