using Microsoft.Extensions.Time.Testing;
using WlanToggle.Core;

namespace WlanToggle.Core.Tests;

public class EngineTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryJournalStore _journal = new();
    private readonly ListSink _log = new();
    private readonly FakeGuard _guard = new();
    private readonly FakeWlan _wlan;
    private readonly List<DisconnectMethod> _built = [];
    private readonly List<WlanToggleConfig> _saved = [];

    public EngineTests() => _wlan = new FakeWlan(_time, _journal);

    private WlanToggleEngine Create(WlanToggleConfig? cfg = null) => new(
        cfg ?? Cfg(),
        m => { _built.Add(m); return _wlan; },
        _journal, _time, _log, _guard, persist: _saved.Add, random: () => 0.5);

    private static WlanToggleConfig Cfg() => new()
    {
        Outage = new OutageOptions
        {
            VerifyTimeout = TimeSpan.FromSeconds(2),
            VerifyPollInterval = TimeSpan.FromMilliseconds(100),
            RetryBackoff = TimeSpan.FromMilliseconds(200),
            EnableAttempts = 2,
        },
    };

    [Fact]
    public void Defaults_match_the_menu_spec()
    {
        var c = new WlanToggleConfig();
        Assert.Equal(DisconnectMethod.Adapter, c.Method);
        Assert.Equal(TimeSpan.FromSeconds(10), c.ReconnectAfter);
        Assert.False(c.PeriodicEnabled);
        Assert.Contains(c.ReconnectAfter, MenuChoices.ReconnectAfter);
        Assert.Contains(c.Periodic.Interval, MenuChoices.PeriodicInterval);
        Assert.All(MenuChoices.ReconnectAfter, d => Assert.True(d <= c.Outage.MaxDown));
        Assert.Equal(6, MenuChoices.ReconnectAfter.Count);
        Assert.Equal(6, MenuChoices.PeriodicInterval.Count);
    }

    [Fact]
    public async Task ToggleNow_disconnects_for_the_configured_time_then_reconnects()
    {
        var e = Create();
        var t = e.ToggleNowAsync();
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(4));

        Assert.True(e.Status.Disconnected);
        Assert.NotNull(e.Status.ReconnectAtUtc);
        Assert.Equal(AdapterState.Down, _wlan.State);

        var outcome = await Drive.RunAsync(t, _time);
        Assert.Equal(OutageResult.Completed, outcome.Result);
        Assert.InRange(outcome.Record!.ActualDown.TotalSeconds, 10, 10.5);
        Assert.False(e.Status.Disconnected);
    }

    [Fact]
    public async Task ToggleNow_while_disconnected_is_reported_busy_not_stacked()
    {
        var e = Create();
        var first = e.ToggleNowAsync();
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(2));
        Assert.Equal(OutageResult.RejectedBusy, (await e.ToggleNowAsync()).Result);
        await Drive.RunAsync(first, _time);
    }

    [Fact]
    public async Task Changing_reconnect_time_applies_to_the_next_toggle_and_is_saved()
    {
        var e = Create();
        Assert.True((await e.SetReconnectAfterAsync(TimeSpan.FromSeconds(30))).Ok);
        Assert.Equal(TimeSpan.FromSeconds(30), _saved.Last().ReconnectAfter);

        var outcome = await Drive.RunAsync(e.ToggleNowAsync(), _time);
        Assert.InRange(outcome.Record!.ActualDown.TotalSeconds, 30, 30.5);
    }

    [Fact]
    public async Task Changing_the_mode_builds_a_controller_for_it_and_is_refused_during_an_outage()
    {
        var e = Create();
        Assert.Equal([DisconnectMethod.Adapter], _built);
        Assert.True((await e.SetMethodAsync(DisconnectMethod.Radio)).Ok);
        Assert.Equal([DisconnectMethod.Adapter, DisconnectMethod.Radio], _built);
        Assert.Equal(DisconnectMethod.Radio, e.Config.Method);

        var t = e.ToggleNowAsync();
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(2));
        var refused = await e.SetMethodAsync(DisconnectMethod.Disassociate);
        Assert.False(refused.Ok);
        Assert.Equal(DisconnectMethod.Radio, e.Config.Method);
        await Drive.RunAsync(t, _time);
    }

    [Fact]
    public async Task Setting_the_same_value_is_a_noop()
    {
        var e = Create();
        Assert.True((await e.SetMethodAsync(DisconnectMethod.Adapter)).Ok);
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Periodic_first_cycle_happens_after_one_interval_not_immediately()
    {
        var e = Create();
        Assert.True((await e.SetPeriodicIntervalAsync(TimeSpan.FromMinutes(1))).Ok);
        Assert.True((await e.SetPeriodicEnabledAsync(true)).Ok);

        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(55));
        Assert.Equal(0, _wlan.DisableCalls);
        Assert.NotNull(e.Status.NextPeriodicUtc);

        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(10));
        Assert.Equal(1, _wlan.DisableCalls);

        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(60));
        Assert.Equal(2, _wlan.DisableCalls);
        await e.ShutdownAsync();
    }

    [Fact]
    public async Task Invalid_combination_is_rejected_and_nothing_changes()
    {
        var e = Create();
        await e.SetPeriodicIntervalAsync(TimeSpan.FromMinutes(5));
        await e.SetReconnectAfterAsync(TimeSpan.FromMinutes(5)); // fine while periodic is off
        var r = await e.SetPeriodicEnabledAsync(true);           // 5 min down in a 5 min interval: no uptime
        Assert.False(r.Ok);
        Assert.False(e.Config.PeriodicEnabled);

        Assert.True((await e.SetPeriodicIntervalAsync(TimeSpan.FromMinutes(15))).Ok);
        Assert.True((await e.SetPeriodicEnabledAsync(true)).Ok);
        Assert.True((await e.SetReconnectAfterAsync(TimeSpan.FromMinutes(10))).Ok);
        Assert.False((await e.SetPeriodicIntervalAsync(TimeSpan.FromMinutes(1))).Ok);
        Assert.Equal(TimeSpan.FromMinutes(15), e.Config.Periodic.Interval);
        await e.ShutdownAsync();
    }

    [Fact]
    public async Task Disabling_periodic_during_an_outage_reconnects_immediately()
    {
        var e = Create(Cfg() with { ReconnectAfter = TimeSpan.FromSeconds(30), Periodic = new PeriodicOptions { Interval = TimeSpan.FromMinutes(1) } });
        await e.SetPeriodicEnabledAsync(true);
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(70));
        Assert.Equal(AdapterState.Down, _wlan.State);

        var off = e.SetPeriodicEnabledAsync(false);
        Assert.True((await Drive.RunAsync(off, _time)).Ok);
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.False(e.Status.Disconnected);
        Assert.False(e.Config.PeriodicEnabled);
    }

    [Fact]
    public async Task Periodic_switches_itself_off_when_the_link_does_not_come_back()
    {
        _wlan.NeverUp = true;
        var e = Create(Cfg() with { PeriodicEnabled = true, Periodic = new PeriodicOptions { Interval = TimeSpan.FromMinutes(1) } });
        await e.StartAsync();
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(100));

        Assert.False(e.Config.PeriodicEnabled);
        Assert.False(_saved.Last().PeriodicEnabled);
        Assert.NotNull(e.Status.LastError);
        Assert.Equal(1, _wlan.DisableCalls);
    }

    [Fact]
    public async Task Start_recovers_a_pending_outage_and_resumes_a_saved_schedule()
    {
        _wlan.State = AdapterState.Down;
        var now = _time.GetUtcNow();
        _journal.Save(new PendingOutage("Wi-Fi", now - TimeSpan.FromSeconds(5), now - TimeSpan.FromSeconds(1), "manual"));
        var e = Create(Cfg() with { PeriodicEnabled = true, Periodic = new PeriodicOptions { Interval = TimeSpan.FromMinutes(5) } });

        await Drive.RunAsync(e.StartAsync(), _time);
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.Null(_journal.Load());
        Assert.NotNull(e.Status.NextPeriodicUtc);
        await e.ShutdownAsync();
    }

    [Fact]
    public async Task RestoreNow_ends_an_outage_early()
    {
        var e = Create(Cfg() with { ReconnectAfter = TimeSpan.FromMinutes(10) });
        var t = e.ToggleNowAsync();
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(3));
        await Drive.RunAsync(e.RestoreNowAsync(), _time);
        Assert.Equal(AdapterState.Up, _wlan.State);
        var outcome = await Drive.RunAsync(t, _time);
        Assert.InRange(outcome.Record!.ActualDown.TotalSeconds, 3, 4);
    }

    [Fact]
    public async Task Changed_event_is_raised_for_state_and_settings_changes()
    {
        var e = Create();
        var n = 0;
        e.Changed += () => Interlocked.Increment(ref n);
        await e.SetReconnectAfterAsync(TimeSpan.FromSeconds(5));
        Assert.True(n >= 1);
    }
}
