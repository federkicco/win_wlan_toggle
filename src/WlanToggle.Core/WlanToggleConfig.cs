using System.Text.Json;
using System.Text.Json.Serialization;

namespace WlanToggle.Core;

public enum DisconnectMethod
{
    /// <summary>Disable/enable the network adapter.</summary>
    Adapter,
    /// <summary>Disassociate from the access point (netsh wlan disconnect) and reconnect.</summary>
    Disassociate,
    /// <summary>Switch the Wi-Fi radio off/on.</summary>
    Radio,
}

public sealed record WlanToggleConfig
{
    public string AdapterName { get; init; } = "Wi-Fi";
    public DisconnectMethod Method { get; init; } = DisconnectMethod.Adapter;
    public OutageOptions Outage { get; init; } = new();

    /// <summary>How long the link stays down before it is brought back ("reconnect interval").</summary>
    public TimeSpan ReconnectAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Run the periodic schedule (also resumed when the app starts).</summary>
    public bool PeriodicEnabled { get; init; }

    /// <summary>Periodic schedule. Its <c>Down</c> is ignored: <see cref="ReconnectAfter"/> is used instead.</summary>
    public PeriodicOptions Periodic { get; init; } = new() { Interval = TimeSpan.FromMinutes(5) };

    /// <summary>The periodic options actually used (down time taken from <see cref="ReconnectAfter"/>).</summary>
    [JsonIgnore]
    public PeriodicOptions EffectivePeriodic => Periodic with { Down = ReconnectAfter };

    public string? LogPath { get; init; }
    public string? JournalPath { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static WlanToggleConfig Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<WlanToggleConfig>(File.ReadAllText(path), Json) ?? new()
            : new();

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Throws if the configuration is unsafe or inconsistent.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AdapterName)) throw new ArgumentException("AdapterName is required.");
        if (Outage.MaxDown <= TimeSpan.Zero) throw new ArgumentException("Outage.MaxDown must be > 0.");
        if (Outage.EnableAttempts < 1) throw new ArgumentException("Outage.EnableAttempts must be >= 1.");
        if (ReconnectAfter <= TimeSpan.Zero || ReconnectAfter > Outage.MaxDown)
            throw new ArgumentException($"ReconnectAfter must be > 0 and <= Outage.MaxDown ({Outage.MaxDown}).");
        // Only an active schedule has to be consistent; this lets the user pick values in any order.
        if (PeriodicEnabled) EffectivePeriodic.Validate(Outage.MaxDown);
    }
}
