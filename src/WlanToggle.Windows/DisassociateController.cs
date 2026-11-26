using System.Net.NetworkInformation;
using WlanToggle.Core;

namespace WlanToggle.Windows;

/// <summary>
/// "Disassociate": leave the access point but keep the adapter enabled. The profile we were connected to is
/// remembered on disk (it must survive a crash) and re-connected explicitly, because Windows does not
/// auto-reconnect after an explicit disconnect.
/// </summary>
public sealed class DisassociateController(NetworkInterface nic, string profileMemoPath) : WifiControllerBase(nic)
{
    public override Task DisableAsync(CancellationToken ct)
    {
        var profile = NativeWifi.GetConnectedProfile(AdapterGuid)
            ?? throw new InvalidOperationException($"'{AdapterName}' is not connected to a Wi-Fi network; nothing to disconnect.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(profileMemoPath))!);
        File.WriteAllText(profileMemoPath, profile);
        NativeWifi.Disconnect(AdapterGuid);
        return Task.CompletedTask;
    }

    public override Task EnableAsync(CancellationToken ct)
    {
        // Make sure the adapter itself is enabled too, in case something else disabled it meanwhile.
        NativeWifi.Connect(AdapterGuid, RememberedProfile());
        return Task.CompletedTask;
    }

    public override async Task EscalateAsync(CancellationToken ct)
    {
        await base.EscalateAsync(ct).ConfigureAwait(false); // bounce the adapter
        await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        NativeWifi.Connect(AdapterGuid, RememberedProfile());
    }

    private string RememberedProfile()
    {
        if (File.Exists(profileMemoPath) && File.ReadAllText(profileMemoPath).Trim() is { Length: > 0 } p) return p;
        throw new InvalidOperationException("no remembered Wi-Fi profile to reconnect to");
    }
}
