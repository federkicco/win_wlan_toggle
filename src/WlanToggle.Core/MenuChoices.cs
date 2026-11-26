namespace WlanToggle.Core;

/// <summary>The fixed choices offered by the tray menu.</summary>
public static class MenuChoices
{
    public static readonly TimeSpan DefaultReconnectAfter = TimeSpan.FromSeconds(10);

    public static readonly IReadOnlyList<TimeSpan> ReconnectAfter =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
    ];

    public static readonly IReadOnlyList<TimeSpan> PeriodicInterval =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1),
    ];

    public static readonly IReadOnlyList<DisconnectMethod> Methods =
        [DisconnectMethod.Adapter, DisconnectMethod.Disassociate, DisconnectMethod.Radio];

    public static string Describe(DisconnectMethod m) => m switch
    {
        DisconnectMethod.Adapter => "Adapter disable",
        DisconnectMethod.Disassociate => "Disassociate (disconnect from AP)",
        DisconnectMethod.Radio => "Radio off",
        _ => m.ToString(),
    };

    public static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? (t.TotalHours == 1 ? "1 hour" : $"{t.TotalHours:F0} hours")
        : t.TotalMinutes >= 1 && t.TotalMinutes % 1 == 0 ? (t.TotalMinutes == 1 ? "1 minute" : $"{t.TotalMinutes:F0} minutes")
        : $"{t.TotalSeconds:F0} seconds";

    /// <summary>A periodic cycle needs the link to be up for some time between disconnects.</summary>
    public static bool IsPeriodicCombinationValid(TimeSpan reconnectAfter, TimeSpan interval) => reconnectAfter < interval;
}
