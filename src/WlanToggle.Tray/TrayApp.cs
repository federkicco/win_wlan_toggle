using Microsoft.Win32;
using WlanToggle.Core;
using WlanToggle.Windows;

namespace WlanToggle.Tray;

/// <summary>
/// Notification-area UI. All behaviour lives in <see cref="WlanToggleEngine"/>; this class only renders its
/// status and forwards clicks.
///   left click  -> disconnect now, reconnect after the configured time
///   right click -> settings menu
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly WlanToggleEngine _engine;
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _periodicItem = new("Enable periodic disconnect") { CheckOnClick = false };
    private readonly ToolStripMenuItem _adapterMenu = new("Adapter");
    private readonly List<(ToolStripMenuItem Item, DisconnectMethod Value)> _methodItems = [];
    private readonly List<(ToolStripMenuItem Item, TimeSpan Value)> _reconnectItems = [];
    private readonly List<(ToolStripMenuItem Item, TimeSpan Value)> _intervalItems = [];

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Directory.CreateDirectory(StatePaths.Directory);

        var config = LoadConfig(out var configProblem);
        // Pin the adapter explicitly: if the configured name does not exist, adopt the connected Wi-Fi adapter and save it.
        var nic = WifiControllers.FindAdapter(config.AdapterName);
        if (!string.Equals(nic.Name, config.AdapterName, StringComparison.OrdinalIgnoreCase))
        {
            configProblem = $"Adapter '{config.AdapterName}' not found; using '{nic.Name}'. Change it via the Adapter menu.";
            config = config with { AdapterName = nic.Name };
            try { config.Save(StatePaths.Config); } catch { }
        }
        _engine = new WlanToggleEngine(
            config,
            WifiControllers.Create,
            new FileJournalStore(StatePaths.Journal),
            TimeProvider.System,
            new CsvOutageLogSink(StatePaths.OutageLog),
            new ScheduledTaskGuard(Environment.ProcessPath!),
            persist: c => c.Save(StatePaths.Config));

        _icon = new NotifyIcon
        {
            Icon = TrayIcons.Connected,
            Text = "WlanToggle",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _ = ToggleNowAsync(); };

        _engine.Changed += () => _ui.Post(_ => Refresh(), null);
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();

        SystemEvents.SessionEnding += (_, _) => EmergencyRestore();
        Refresh();

        if (configProblem is not null) Notify(configProblem, ToolTipIcon.Warning);
        _ = StartAsync();
    }

    private static WlanToggleConfig LoadConfig(out string? problem)
    {
        problem = null;
        try
        {
            var c = WlanToggleConfig.Load(StatePaths.Config);
            c.Validate();
            return c;
        }
        catch (Exception ex)
        {
            problem = "Settings file was invalid, defaults used: " + ex.Message;
            return new WlanToggleConfig();
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await _engine.StartAsync();
            Notify($"Left-click: toggle Wi-Fi ({MenuChoices.Describe(_engine.Config.ReconnectAfter)}). Right-click: settings.", ToolTipIcon.Info);
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    // ---- menu -------------------------------------------------------------------------------------------

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => Refresh();

        // The adapter list is rebuilt each time the menu opens (adapters come and go).
        menu.Opening += (_, _) => RebuildAdapterMenu();

        var mode = new ToolStripMenuItem("Disconnect mode");
        foreach (var m in MenuChoices.Methods)
        {
            var item = new ToolStripMenuItem(MenuChoices.Describe(m));
            item.Click += (_, _) => Apply(_engine.SetMethodAsync(m));
            mode.DropDownItems.Add(item);
            _methodItems.Add((item, m));
        }

        var reconnect = new ToolStripMenuItem("Reconnect after");
        foreach (var t in MenuChoices.ReconnectAfter)
        {
            var item = new ToolStripMenuItem(MenuChoices.Describe(t));
            item.Click += (_, _) => Apply(_engine.SetReconnectAfterAsync(t));
            reconnect.DropDownItems.Add(item);
            _reconnectItems.Add((item, t));
        }

        _periodicItem.Click += (_, _) => Apply(_engine.SetPeriodicEnabledAsync(!_engine.Config.PeriodicEnabled));

        var interval = new ToolStripMenuItem("Periodic disconnect interval");
        foreach (var t in MenuChoices.PeriodicInterval)
        {
            var item = new ToolStripMenuItem(MenuChoices.Describe(t));
            item.Click += (_, _) => Apply(_engine.SetPeriodicIntervalAsync(t));
            interval.DropDownItems.Add(item);
            _intervalItems.Add((item, t));
        }

        var restore = new ToolStripMenuItem("Reconnect now");
        restore.Click += async (_, _) => await RestoreNowAsync();

        var openFolder = new ToolStripMenuItem("Open log folder");
        openFolder.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(StatePaths.Directory) { UseShellExecute = true });

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += async (_, _) => await ExitAsync();

        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem, new ToolStripSeparator(),
            _adapterMenu, mode, reconnect, _periodicItem, interval, new ToolStripSeparator(),
            restore, openFolder, exit,
        });
        return menu;
    }

    private void RebuildAdapterMenu()
    {
        _adapterMenu.DropDownItems.Clear();
        var current = _engine.Status.AdapterName;
        IReadOnlyList<WifiControllers.AdapterInfo> adapters;
        try { adapters = WifiControllers.ListWirelessAdapters(); }
        catch { adapters = []; }

        foreach (var a in adapters)
        {
            var item = new ToolStripMenuItem($"{a.Name}  -  {a.Description}" + (a.IsUp ? "  (connected)" : ""))
            {
                Checked = string.Equals(a.Name, current, StringComparison.OrdinalIgnoreCase),
                Enabled = !_engine.Status.Disconnected,
            };
            var name = a.Name;
            item.Click += (_, _) => Apply(_engine.SetAdapterAsync(name));
            _adapterMenu.DropDownItems.Add(item);
        }
        if (adapters.Count == 0)
            _adapterMenu.DropDownItems.Add(new ToolStripMenuItem("(no Wi-Fi adapters found)") { Enabled = false });
    }

    private void Refresh()
    {
        var cfg = _engine.Config;
        var st = _engine.Status;
        var now = DateTimeOffset.UtcNow;

        string text;
        if (st.Disconnected)
        {
            var left = Math.Max(0, (int)Math.Ceiling(((st.ReconnectAtUtc ?? now) - now).TotalSeconds));
            text = $"Disconnected, reconnecting in {Format(left)}";
        }
        else if (st.NextPeriodicUtc is { } next)
            text = $"Connected. Next periodic disconnect in {Format(Math.Max(0, (int)(next - now).TotalSeconds))}";
        else
            text = "Connected";
        text = $"[{st.AdapterName}] {text}";
        if (st.LastError is not null) text += " (last error: " + st.LastError + ")";

        _statusItem.Text = text.Length > 120 ? text[..117] + "..." : text;
        _icon.Icon = st.Disconnected ? TrayIcons.Disconnected : TrayIcons.Connected;
        _icon.Text = (text.Length > 120 ? text[..117] + "..." : text);

        foreach (var (item, m) in _methodItems)
        {
            item.Checked = cfg.Method == m;
            item.Enabled = !st.Disconnected; // cannot swap the mechanism while the link is down
        }
        foreach (var (item, t) in _reconnectItems)
        {
            item.Checked = cfg.ReconnectAfter == t;
            item.Enabled = !cfg.PeriodicEnabled || MenuChoices.IsPeriodicCombinationValid(t, cfg.Periodic.Interval);
        }
        _periodicItem.Checked = cfg.PeriodicEnabled;
        foreach (var (item, t) in _intervalItems)
        {
            item.Checked = cfg.Periodic.Interval == t;
            item.Enabled = !cfg.PeriodicEnabled || MenuChoices.IsPeriodicCombinationValid(cfg.ReconnectAfter, t);
        }
    }

    private static string Format(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}h {seconds % 3600 / 60}m"
        : seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s"
        : $"{seconds}s";

    // ---- actions ----------------------------------------------------------------------------------------

    private async Task ToggleNowAsync()
    {
        try
        {
            var outcome = await _engine.ToggleNowAsync();
            switch (outcome.Result)
            {
                case OutageResult.RejectedBusy:
                    Notify("Already disconnected; it will reconnect on its own (use \"Reconnect now\" to end it early).", ToolTipIcon.Info);
                    break;
                case OutageResult.Failed:
                    Notify($"Toggle failed on adapter '{_engine.Status.AdapterName}': " + outcome.Record?.Error, ToolTipIcon.Error);
                    break;
            }
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private async Task RestoreNowAsync()
    {
        try
        {
            var o = await _engine.RestoreNowAsync();
            if (o.Result == OutageResult.Failed) Notify("Could not bring Wi-Fi back: " + o.Record?.Error, ToolTipIcon.Error);
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private async void Apply(Task<SetResult> change)
    {
        try
        {
            var r = await change;
            if (!r.Ok) Notify(r.Error ?? "Setting not applied.", ToolTipIcon.Warning);
        }
        catch (Exception ex) { ReportError(ex.Message); }
        Refresh();
    }

    private async Task ExitAsync()
    {
        _tick.Stop();
        try { await _engine.ShutdownAsync(); } catch { }
        _icon.Visible = false;
        _icon.Dispose();
        ExitThread();
    }

    /// <summary>Synchronous best-effort restore for logoff / shutdown / crash paths.</summary>
    public void EmergencyRestore()
    {
        try { _engine.ShutdownAsync().Wait(TimeSpan.FromSeconds(60)); } catch { }
    }

    public void ReportError(string message) => Notify(message, ToolTipIcon.Error);

    private void Notify(string message, ToolTipIcon icon)
    {
        _ui.Post(_ =>
        {
            _icon.BalloonTipTitle = "WlanToggle";
            _icon.BalloonTipText = message.Length > 250 ? message[..247] + "..." : message;
            _icon.BalloonTipIcon = icon;
            _icon.ShowBalloonTip(5000);
        }, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _icon.Dispose(); }
        base.Dispose(disposing);
    }
}
