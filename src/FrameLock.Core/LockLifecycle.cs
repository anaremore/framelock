namespace FrameLock.Core;

public enum LockStatus
{
    Unlocked,
    Locking,
    Locked,
    Suspended,
    TargetClosed,
    Faulted,
}

public enum LockSignal
{
    BeginLock,
    SizeVerified,
    TargetMinimized,
    TargetRestored,
    TargetDestroyed,
    Unlock,
    Fail,
}

public sealed record LockLifecycleState(LockStatus Status, string? Message = null)
{
    public static LockLifecycleState Initial { get; } = new(LockStatus.Unlocked);

    public bool IsActive => Status is LockStatus.Locking or LockStatus.Locked or LockStatus.Suspended;
}

public static class LockLifecycle
{
    public static LockLifecycleState Reduce(
        LockLifecycleState state,
        LockSignal signal,
        string? message = null) =>
        (state.Status, signal) switch
        {
            (_, LockSignal.BeginLock) when !state.IsActive => new(LockStatus.Locking),
            (LockStatus.Locking, LockSignal.SizeVerified) => new(LockStatus.Locked),
            (LockStatus.Locked, LockSignal.SizeVerified) => state,
            (LockStatus.Locked, LockSignal.TargetMinimized) =>
                new(LockStatus.Suspended, "The target is minimized. Size locking will resume when it is restored."),
            (LockStatus.Suspended, LockSignal.TargetRestored) => new(LockStatus.Locked),
            (_, LockSignal.TargetDestroyed) when state.IsActive =>
                new(LockStatus.TargetClosed, "The target window closed. FrameLock unlocked it safely."),
            (_, LockSignal.Fail) when state.IsActive => new(LockStatus.Faulted, message ?? "FrameLock could not keep this window locked."),
            (_, LockSignal.Unlock) => LockLifecycleState.Initial,
            _ => state,
        };
}
