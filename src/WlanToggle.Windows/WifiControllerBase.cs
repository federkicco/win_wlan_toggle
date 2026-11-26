using System.Net.NetworkInformation;
using WlanToggle.Core;

namespace WlanToggle.Windows;

public abstract class WifiControllerBase : IWlanController
{
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(30);

    protected WifiControllerBase(NetworkInterface nic)
    {
        AdapterName = nic.Name;
        AdapterGuid = Guid.TryParse(nic.Id, out var g) ? g : Guid.Empty;
    }

    public string AdapterName { get; }
    protected Guid AdapterGuid { get; }

    public abstract Task DisableAsync(CancellationToken ct);
    public abstract Task EnableAsync(CancellationToken ct);

    /// <summary>Default escalation: bounce the adapter itself.</summary>
    public virtual async Task EscalateAsync(CancellationToken ct)
    {
        await SetAdminStateAsync(false, ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        await SetAdminStateAsync(true, ct).ConfigureAwait(false);
    }

    /// <summary>Up = the adapter exists and its link is operationally up (associated, for Wi-Fi).</summary>
    public Task<AdapterState> GetStateAsync(CancellationToken ct)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, AdapterName, StringComparison.OrdinalIgnoreCase));
        var state = nic is null ? AdapterState.Unknown
            : nic.OperationalStatus == OperationalStatus.Up ? AdapterState.Up : AdapterState.Down;
        return Task.FromResult(state);
    }

    protected Task SetAdminStateAsync(bool enabled, CancellationToken ct) =>
        ProcessRunner.RunAsync("netsh.exe",
            ["interface", "set", "interface", $"name={AdapterName}", $"admin={(enabled ? "enabled" : "disabled")}"],
            NetshTimeout, ct);
}
