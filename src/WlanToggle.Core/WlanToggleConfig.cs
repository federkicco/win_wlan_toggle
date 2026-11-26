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
    public PeriodicOptions Periodic { get; init; } = new();

    /// <summary>Resume the periodic schedule automatically when the service starts.</summary>
    public bool ResumePeriodicOnStart { get; init; }

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
        Periodic.Validate(Outage.MaxDown);
    }
}
