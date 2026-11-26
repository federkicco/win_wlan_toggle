using System.Net.NetworkInformation;
using WlanToggle.Core;

namespace WlanToggle.Windows;

public static class WifiControllers
{
    public sealed record AdapterInfo(string Name, string Description, bool IsUp);

    /// <summary>All wireless adapters, connected ones first.</summary>
    public static IReadOnlyList<AdapterInfo> ListWirelessAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            .Select(n => new AdapterInfo(n.Name, n.Description, n.OperationalStatus == OperationalStatus.Up))
            .OrderByDescending(a => a.IsUp).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Finds the adapter called <paramref name="configuredName"/> (exact, case-insensitive). If there is none, falls
    /// back to the wireless adapter that is currently connected, else any wireless adapter. Throws when the machine
    /// has no Wi-Fi adapter. The caller should persist the resolved name so later lookups are exact.
    /// </summary>
    public static NetworkInterface FindAdapter(string configuredName)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();
        var exact = all.FirstOrDefault(n => string.Equals(n.Name, configuredName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var wireless = all.Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211).ToList();
        return wireless.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up)
            ?? wireless.FirstOrDefault()
            ?? throw new InvalidOperationException("No Wi-Fi adapter found on this machine.");
    }

    public static IWlanController Create(WlanToggleConfig config)
    {
        var nic = FindAdapter(config.AdapterName);
        return config.Method switch
        {
            DisconnectMethod.Adapter => new AdapterController(nic),
            DisconnectMethod.Disassociate => new DisassociateController(nic, StatePaths.ProfileMemo),
            DisconnectMethod.Radio => new RadioController(nic),
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
    }
}
