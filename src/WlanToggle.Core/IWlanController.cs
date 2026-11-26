namespace WlanToggle.Core;

public enum AdapterState
{
    Unknown,
    /// <summary>Adapter is enabled and connected (or at least operationally up).</summary>
    Up,
    /// <summary>Adapter is disabled / disconnected.</summary>
    Down,
}

/// <summary>
/// Abstraction over the platform-specific mechanism that takes the Wi-Fi link down and up.
/// Windows implementations (adapter disable, netsh disconnect, radio off) live outside Core.
/// </summary>
public interface IWlanController
{
    /// <summary>Name of the managed adapter (used for the journal and log).</summary>
    string AdapterName { get; }

    Task<AdapterState> GetStateAsync(CancellationToken ct);
    Task DisableAsync(CancellationToken ct);
    Task EnableAsync(CancellationToken ct);

    /// <summary>
    /// Stronger recovery used when a plain <see cref="EnableAsync"/> did not bring the link up
    /// (e.g. restart the adapter, re-issue "netsh wlan connect").
    /// </summary>
    Task EscalateAsync(CancellationToken ct);
}
