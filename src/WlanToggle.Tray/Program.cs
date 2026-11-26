using WlanToggle.Core;
using WlanToggle.Windows;

namespace WlanToggle.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Headless mode used by the SYSTEM backstop task: make sure the Wi-Fi link is up, then exit.
        if (args.Length > 0 && args[0].Equals("restore", StringComparison.OrdinalIgnoreCase))
            return RestoreAsync().GetAwaiter().GetResult();

        using var singleInstance = new Mutex(initiallyOwned: true, @"Global\WlanToggleTray", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("WlanToggle is already running (look for its icon in the notification area).",
                "WlanToggle", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        TrayApp app;
        try { app = new TrayApp(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "WlanToggle", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // Last-ditch: whatever kills us, try to leave the link up.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => app.EmergencyRestore();
        Application.ThreadException += (_, e) => app.ReportError(e.Exception.Message);

        Application.Run(app);
        return 0;
    }

    private static async Task<int> RestoreAsync()
    {
        try
        {
            Directory.CreateDirectory(StatePaths.Directory);
            var cfg = WlanToggleConfig.Load(StatePaths.Config);
            var manager = new OutageManager(
                WifiControllers.Create(cfg),
                new FileJournalStore(StatePaths.Journal), TimeProvider.System, cfg.Outage,
                new CsvOutageLogSink(StatePaths.OutageLog),
                new ScheduledTaskGuard(Environment.ProcessPath!));
            var outcome = await manager.RestoreNowAsync();
            return outcome.Result == OutageResult.Completed ? 0 : 1;
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(StatePaths.Directory, "restore-error.log"), $"{DateTimeOffset.Now:O} {ex}\n"); } catch { }
            return 2;
        }
    }
}
