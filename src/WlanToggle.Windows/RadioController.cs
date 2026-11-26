using System.Net.NetworkInformation;
using Windows.Devices.Radios;

namespace WlanToggle.Windows;

/// <summary>"Radio off": soft-switch the Wi-Fi radio (like airplane mode for Wi-Fi only) via the WinRT Radios API.</summary>
public sealed class RadioController(NetworkInterface nic) : WifiControllerBase(nic)
{
    public override Task DisableAsync(CancellationToken ct) => SetAsync(RadioState.Off);
    public override Task EnableAsync(CancellationToken ct) => SetAsync(RadioState.On);

    private static async Task SetAsync(RadioState state)
    {
        var access = await Radio.RequestAccessAsync();
        if (access != RadioAccessStatus.Allowed)
            throw new InvalidOperationException($"Windows denied access to the radio ({access}).");

        var wifi = (await Radio.GetRadiosAsync()).Where(r => r.Kind == RadioKind.WiFi).ToList();
        if (wifi.Count == 0) throw new InvalidOperationException("no Wi-Fi radio found");

        foreach (var radio in wifi)
        {
            var result = await radio.SetStateAsync(state);
            if (result != RadioAccessStatus.Allowed)
                throw new InvalidOperationException($"could not switch the radio {state}: {result}");
        }
    }

    public override async Task EscalateAsync(CancellationToken ct)
    {
        try { await SetAsync(RadioState.On).ConfigureAwait(false); } catch { /* fall through to the adapter bounce */ }
        await base.EscalateAsync(ct).ConfigureAwait(false);
    }
}
