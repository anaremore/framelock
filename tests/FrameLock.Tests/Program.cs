using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using FrameLock.Core;
using FrameLock.Windows;

namespace FrameLock.Tests;

internal static class Program
{
    private sealed record TestCase(string Name, Func<Task> Run);

    public static async Task<int> Main(string[] args)
    {
        var tests = new List<TestCase>
        {
            new("resolution parsing accepts valid dimensions", TestResolutionParsing),
            new("resolution parsing rejects invalid dimensions", TestResolutionValidation),
            new("resolution presets match the product contract", TestResolutionPresets),
            new("outer-size estimate preserves non-client frame", TestOuterSizeEstimate),
            new("correction math converges from measured error", TestCorrectionMath),
            new("lock lifecycle handles minimize, restore, and close", TestLockLifecycle),
            new("restore policy rejects recycled targets", TestRestorePolicy),
            new("preferences remember global and per-app resolutions", TestPreferenceSelection),
            new("preferences round-trip and overwrite atomically", TestPreferencePersistence),
            new("malformed preferences recover to safe defaults", TestMalformedPreferences),
        };

        if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
        {
            tests.AddRange(
            [
                new("integration: discovery and exact client sizing", TestExactClientSizingIntegration),
                new("integration: lock corrects a target resize and restores on unlock", TestPersistentLockIntegration),
                new("integration: WinEvent hook corrects before the watchdog", TestWinEventHookIntegration),
                new("integration: minimize pauses and restore resumes locking", TestMinimizeRestoreIntegration),
                new("integration: target close transitions safely", TestTargetCloseIntegration),
            ]);
        }

        Console.WriteLine($"FrameLock validation · {tests.Count} tests");
        var failures = 0;
        foreach (var test in tests)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await test.Run();
                Console.WriteLine($"PASS  {test.Name} ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"FAIL  {test.Name}");
                Console.WriteLine($"      {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine(failures == 0
            ? $"All {tests.Count} tests passed."
            : $"{failures} of {tests.Count} tests failed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestResolutionParsing()
    {
        Assert.True(Resolution.TryParse("1920", "1080", out var result, out var error));
        Assert.Equal(new Resolution(1920, 1080), result);
        Assert.Null(error);
        return Task.CompletedTask;
    }

    private static Task TestResolutionValidation()
    {
        Assert.False(Resolution.TryParse("abc", "1080", out _, out var textError));
        Assert.Contains("whole-number", textError);
        Assert.False(Resolution.TryParse("159", "1080", out _, out var rangeError));
        Assert.Contains("between", rangeError);
        Assert.False(new Resolution(16_385, 1080).IsValid);
        return Task.CompletedTask;
    }

    private static Task TestResolutionPresets()
    {
        Resolution[] expected =
        [
            new(3840, 2160),
            new(2560, 1440),
            new(1920, 1080),
            new(1600, 900),
            new(1280, 720),
            new(1080, 1920),
        ];
        Assert.SequenceEqual(expected, Resolution.Presets);
        return Task.CompletedTask;
    }

    private static Task TestOuterSizeEstimate()
    {
        var outer = WindowSizingMath.EstimateOuterSize(
            new Resolution(1920, 1080),
            new Resolution(800, 600),
            new Resolution(816, 647));
        Assert.Equal(new Resolution(1936, 1127), outer);
        return Task.CompletedTask;
    }

    private static Task TestCorrectionMath()
    {
        var corrected = WindowSizingMath.CorrectOuterSize(
            new Resolution(1920, 1080),
            new Resolution(1918, 1078),
            new Resolution(1934, 1125));
        Assert.Equal(new Resolution(1936, 1127), corrected);
        return Task.CompletedTask;
    }

    private static Task TestLockLifecycle()
    {
        var state = LockLifecycle.Reduce(LockLifecycleState.Initial, LockSignal.BeginLock);
        Assert.Equal(LockStatus.Locking, state.Status);
        state = LockLifecycle.Reduce(state, LockSignal.SizeVerified);
        Assert.Equal(LockStatus.Locked, state.Status);
        state = LockLifecycle.Reduce(state, LockSignal.TargetMinimized);
        Assert.Equal(LockStatus.Suspended, state.Status);
        state = LockLifecycle.Reduce(state, LockSignal.TargetRestored);
        Assert.Equal(LockStatus.Locked, state.Status);
        state = LockLifecycle.Reduce(state, LockSignal.TargetDestroyed);
        Assert.Equal(LockStatus.TargetClosed, state.Status);
        state = LockLifecycle.Reduce(state, LockSignal.BeginLock);
        Assert.Equal(LockStatus.Locking, state.Status);
        state = LockLifecycle.Reduce(state, LockSignal.Unlock);
        Assert.Equal(LockStatus.Unlocked, state.Status);
        return Task.CompletedTask;
    }

    private static Task TestRestorePolicy()
    {
        var captured = new TargetStamp((nint)1234, 42, 1000);
        Assert.True(RestorePolicy.CanRestore(captured, captured, isWindow: true));
        Assert.False(RestorePolicy.CanRestore(captured, captured with { ProcessId = 43 }, isWindow: true));
        Assert.False(RestorePolicy.CanRestore(captured, captured with { ProcessStartTimeUtcTicks = 1001 }, isWindow: true));
        Assert.False(RestorePolicy.CanRestore(captured, captured, isWindow: false));
        return Task.CompletedTask;
    }

    private static Task TestPreferenceSelection()
    {
        var preferences = new FrameLockPreferences();
        preferences.Remember("chrome.exe", new Resolution(2560, 1440));
        preferences.Remember("code.exe", new Resolution(1600, 900));
        Assert.Equal(new Resolution(2560, 1440), preferences.ResolutionFor("CHROME.EXE"));
        Assert.Equal(new Resolution(1600, 900), preferences.ResolutionFor("unknown.exe"));
        return Task.CompletedTask;
    }

    private static Task TestPreferencePersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"FrameLock.Tests.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new JsonPreferencesStore(path);
            var preferences = new FrameLockPreferences();
            preferences.Remember("app.exe", new Resolution(1280, 720));
            store.Save(preferences);

            var loaded = store.Load();
            Assert.Equal(new Resolution(1280, 720), loaded.LastResolution);
            Assert.Equal(new Resolution(1280, 720), loaded.ResolutionFor("APP.EXE"));

            loaded.Remember("app.exe", new Resolution(1920, 1080));
            store.Save(loaded);
            Assert.Equal(new Resolution(1920, 1080), store.Load().ResolutionFor("app.exe"));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestMalformedPreferences()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"FrameLock.Tests.{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{this is not json");
            var loaded = new JsonPreferencesStore(path).Load();
            Assert.Equal(new Resolution(1920, 1080), loaded.LastResolution);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return Task.CompletedTask;
    }

    private static async Task TestExactClientSizingIntegration()
    {
        using var harness = HarnessProcess.Start();
        var discovered = WindowDiscoveryService.GetWindows()
            .SingleOrDefault(window => window.ProcessId == harness.Target.ProcessId);
        Assert.NotNull(discovered);

        Resolution[] sizes =
        [
            new(1280, 720),
            new(1920, 1080),
            new(2560, 1440),
            new(1080, 1920),
            new(1234, 777),
        ];
        foreach (var size in sizes)
        {
            var result = WindowSizingService.SetClientSize(harness.Target, size);
            Assert.Equal(size, result.Metrics.Client);
            Assert.True(result.IsExact);
        }

        _ = WindowSizingService.Center(harness.Target);
        await Task.CompletedTask;
    }

    private static async Task TestPersistentLockIntegration()
    {
        using var harness = HarnessProcess.Start("--resize-once=1200");
        var original = WindowSizingService.GetMetrics(harness.Target).Client;
        using var lockService = new WindowLockService();
        var desired = new Resolution(1280, 720);
        var initial = await lockService.LockAsync(harness.Target, desired);
        Assert.Equal(desired, initial.Metrics.Client);

        await Task.Delay(3200);
        Assert.Equal(desired, WindowSizingService.GetMetrics(harness.Target).Client);
        await lockService.UnlockAsync(restore: true);
        await Task.Delay(150);
        Assert.Equal(original, WindowSizingService.GetMetrics(harness.Target).Client);
        Assert.Equal(LockStatus.Unlocked, lockService.State.Status);
    }

    private static async Task TestWinEventHookIntegration()
    {
        using var harness = HarnessProcess.Start();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcherThread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    using var lockService = new WindowLockService();
                    var desired = new Resolution(1280, 720);
                    _ = await lockService.LockAsync(harness.Target, desired);
                    var wrongSize = new Resolution(900, 600);
                    _ = await Task.Run(() => WindowSizingService.SetClientSize(harness.Target, wrongSize));
                    Assert.Equal(wrongSize, WindowSizingService.GetMetrics(harness.Target).Client);

                    await Task.Delay(500);
                    Assert.Equal(desired, WindowSizingService.GetMetrics(harness.Target).Client);
                    await lockService.UnlockAsync(restore: true);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.True(dispatcherThread.Join(TimeSpan.FromSeconds(2)), "The WinEvent dispatcher thread did not exit cleanly.");
    }

    private static async Task TestMinimizeRestoreIntegration()
    {
        using var harness = HarnessProcess.Start("--minimize-after=700", "--restore-after=2300");
        using var lockService = new WindowLockService();
        var desired = new Resolution(1600, 900);
        _ = await lockService.LockAsync(harness.Target, desired);
        await Task.Delay(3600);
        Assert.Equal(LockStatus.Locked, lockService.State.Status);
        Assert.Equal(desired, WindowSizingService.GetMetrics(harness.Target).Client);
        await lockService.UnlockAsync(restore: true);
    }

    private static async Task TestTargetCloseIntegration()
    {
        using var harness = HarnessProcess.Start("--close-after=900");
        using var lockService = new WindowLockService();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lockService.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.State.Status == LockStatus.TargetClosed)
            {
                closed.TrySetResult();
            }
        };

        _ = await lockService.LockAsync(harness.Target, new Resolution(1280, 720));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LockStatus.TargetClosed, lockService.State.Status);
    }

    private sealed class HarnessProcess : IDisposable
    {
        private readonly Process _process;
        private bool _disposed;

        private HarnessProcess(Process process, WindowTarget target)
        {
            _process = process;
            Target = target;
        }

        internal WindowTarget Target { get; }

        internal static HarnessProcess Start(params string[] arguments)
        {
            var harnessPath = FindHarnessPath();
            var startInfo = new ProcessStartInfo(harnessPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(harnessPath)!,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not launch the integration harness.");
            try
            {
                _ = process.WaitForInputIdle(5000);
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException("The integration harness exited before creating a window.");
                    }

                    if (process.MainWindowHandle != 0)
                    {
                        var target = new WindowTarget(
                            process.MainWindowHandle,
                            checked((uint)process.Id),
                            process.StartTime.ToUniversalTime().Ticks,
                            process.ProcessName,
                            "FrameLock Test Harness",
                            "FrameLock Integration Target",
                            harnessPath,
                            harnessPath.ToUpperInvariant());
                        return new HarnessProcess(process, target);
                    }

                    Thread.Sleep(100);
                }

                throw new TimeoutException("The integration harness did not create a window in time.");
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!_process.HasExited)
            {
                _ = _process.CloseMainWindow();
                if (!_process.WaitForExit(1500))
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit();
                }
            }

            _process.Dispose();
            _disposed = true;
        }

        private static string FindHarnessPath()
        {
            var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            var configuration = baseDirectory.Parent?.Name ?? "Debug";
            var root = baseDirectory;
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "FrameLock.slnx")))
            {
                root = root.Parent;
            }

            if (root is null)
            {
                throw new DirectoryNotFoundException("Could not locate the FrameLock repository root.");
            }

            var path = Path.Combine(
                root.FullName,
                "tests",
                "FrameLock.TestHarness",
                "bin",
                configuration,
                "net10.0-windows",
                "FrameLock.TestHarness.exe");
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException("Build FrameLock.TestHarness before running integration tests.", path);
        }
    }

    private static class Assert
    {
        internal static void True(bool condition, string? message = null)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message ?? "Expected true but found false.");
            }
        }

        internal static void False(bool condition, string? message = null) => True(!condition, message ?? "Expected false but found true.");

        internal static void Null(object? value)
        {
            if (value is not null)
            {
                throw new InvalidOperationException($"Expected null but found {value}.");
            }
        }

        internal static void NotNull(object? value)
        {
            if (value is null)
            {
                throw new InvalidOperationException("Expected a value but found null.");
            }
        }

        internal static void Equal<T>(T expected, T actual)
            where T : notnull
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"Expected {expected} but found {actual}.");
            }
        }

        internal static void Contains(string expectedSubstring, string? actual)
        {
            if (actual is null || !actual.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Expected '{actual}' to contain '{expectedSubstring}'.");
            }
        }

        internal static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        {
            if (!expected.SequenceEqual(actual))
            {
                throw new InvalidOperationException("Sequences did not match.");
            }
        }
    }
}
