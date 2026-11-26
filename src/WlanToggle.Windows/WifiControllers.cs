using System.Net.NetworkInformation;
using WlanToggle.Core;

namespace WlanToggle.Windows;

public static class WifiControllers
{
    /// <summary>
    /// Finds the adapter called <paramref name="configuredName"/>; if there is none, falls back to the first
    /// wireless adapter. Throws when the machine has no Wi-Fi adapter.
    /// </summary>
    public static NetworkInterface FindAdapter(string configuredName)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();
        return all.FirstOrDefault(n => string.Equals(n.Name, configuredName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            ?? throw new InvalidOperationException("No Wi-Fi adapter found on this machine.");
    }

    public static IWlanController Create(DisconnectMethod method, string configuredAdapterName)
    {
        var nic = FindAdapter(configuredAdapterName);
        return method switch
        {
            DisconnectMethod.Adapter => new AdapterController(nic),
            DisconnectMethod.Disassociate => new DisassociateController(nic, StatePaths.ProfileMemo),
            DisconnectMethod.Radio => new RadioController(nic),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };
    }
}
