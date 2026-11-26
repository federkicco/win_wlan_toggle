namespace WlanToggle.Core;

/// <summary>
/// An independent safety net that lives OUTSIDE the process doing the toggling (e.g. a SYSTEM scheduled task
/// that runs "restore" shortly after the deadline). It is armed before the link goes down and disarmed once the
/// link is verified up, so a crash, kill or logoff mid-outage cannot strand the machine offline.
/// </summary>
public interface IOutageGuard
{
    /// <summary>Make sure the link gets restored shortly after <paramref name="latestReenableUtc"/> no matter what.</summary>
    Task ArmAsync(DateTimeOffset latestReenableUtc, string adapterName);

    Task DisarmAsync();
}

public sealed class NullOutageGuard : IOutageGuard
{
    public Task ArmAsync(DateTimeOffset latestReenableUtc, string adapterName) => Task.CompletedTask;
    public Task DisarmAsync() => Task.CompletedTask;
}
