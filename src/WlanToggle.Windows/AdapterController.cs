using System.Net.NetworkInformation;

namespace WlanToggle.Windows;

/// <summary>"Adapter disable": full teardown via netsh. Slowest to come back (driver re-init) but the most thorough.</summary>
public sealed class AdapterController(NetworkInterface nic) : WifiControllerBase(nic)
{
    public override Task DisableAsync(CancellationToken ct) => SetAdminStateAsync(false, ct);
    public override Task EnableAsync(CancellationToken ct) => SetAdminStateAsync(true, ct);
}
