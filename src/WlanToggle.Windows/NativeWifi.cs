using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WlanToggle.Windows;

/// <summary>Minimal wrapper over the Native Wifi API (wlanapi.dll): locale-independent connect/disconnect.</summary>
internal static class NativeWifi
{
    private const int ErrorSuccess = 0;
    private const int ErrorInvalidState = 5023; // not connected / interface not in a state that has the data
    private const int CurrentConnectionOpcode = 7; // wlan_intf_opcode_current_connection

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")] private static extern int WlanDisconnect(IntPtr handle, ref Guid iface, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanConnect(IntPtr handle, ref Guid iface, ref ConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionParameters
    {
        public int Mode;              // wlan_connection_mode_profile = 0
        public IntPtr Profile;        // LPCWSTR
        public IntPtr Ssid;           // PDOT11_SSID (unused for profile mode)
        public IntPtr BssidList;      // unused
        public int BssType;           // dot11_BSS_type_any = 3
        public int Flags;
    }

    private static T WithHandle<T>(Func<IntPtr, T> action)
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out var handle), "WlanOpenHandle");
        try { return action(handle); }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    private static void Check(int code, string what)
    {
        if (code != ErrorSuccess) throw new Win32Exception(code, $"{what} failed");
    }

    /// <summary>Profile name of the network the interface is currently connected to, or null if not connected.</summary>
    public static string? GetConnectedProfile(Guid iface) => WithHandle(h =>
    {
        var code = WlanQueryInterface(h, ref iface, CurrentConnectionOpcode, IntPtr.Zero, out _, out var data, IntPtr.Zero);
        if (code == ErrorInvalidState) return null;
        Check(code, "WlanQueryInterface");
        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: { int isState; int connectionMode; WCHAR strProfileName[256]; ... }
            var state = Marshal.ReadInt32(data);
            if (state != 1) return null; // wlan_interface_state_connected
            var name = Marshal.PtrToStringUni(data + 8);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        finally { WlanFreeMemory(data); }
    });

    public static void Disconnect(Guid iface) => WithHandle(h =>
    {
        Check(WlanDisconnect(h, ref iface, IntPtr.Zero), "WlanDisconnect");
        return 0;
    });

    /// <summary>Asks Windows to connect using a saved profile. Returns immediately; association completes asynchronously.</summary>
    public static void Connect(Guid iface, string profile) => WithHandle(h =>
    {
        var p = new ConnectionParameters { Mode = 0, Profile = Marshal.StringToHGlobalUni(profile), BssType = 3 };
        try { Check(WlanConnect(h, ref iface, ref p, IntPtr.Zero), "WlanConnect"); }
        finally { Marshal.FreeHGlobal(p.Profile); }
        return 0;
    });
}
