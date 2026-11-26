namespace WlanToggle.Windows;

/// <summary>Where config, journal and logs live (machine-wide, so the SYSTEM backstop sees the same files as the tray).</summary>
public static class StatePaths
{
    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WlanToggle");

    public static string Config => Path.Combine(Directory, "config.json");
    public static string Journal => Path.Combine(Directory, "state.json");
    public static string OutageLog => Path.Combine(Directory, "outages.csv");
    public static string ProfileMemo => Path.Combine(Directory, "wifi-profile.txt");
}
