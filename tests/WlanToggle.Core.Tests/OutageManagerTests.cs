using Microsoft.Extensions.Time.Testing;
using WlanToggle.Core;

namespace WlanToggle.Core.Tests;

public class OutageManagerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryJournalStore _journal = new();
    private readonly ListSink _log = new();
    private readonly FakeWlan _wlan;

    public OutageManagerTests() => _wlan = new FakeWlan(_time, _journal);

    private readonly FakeGuard _guard = new();

    private OutageManager Create(OutageOptions? o = null) => new(_wlan, _journal, _time, o ?? Fast(), _log, _guard);

    private static OutageOptions Fast() => new()
    {
        VerifyTimeout = TimeSpan.FromSeconds(2),
        VerifyPollInterval = TimeSpan.FromMilliseconds(100),
        RetryBackoff = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Happy_path_goes_down_then_up_and_clears_journal()
    {
        var m = Create();
        var t = m.RunOutageAsync(TimeSpan.FromSeconds(10), "manual");

        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(5));
        Assert.Equal(AdapterState.Down, _wlan.State);
        Assert.NotNull(_journal.Load());
        Assert.NotNull(m.Current);

        var outcome = await Drive.RunAsync(t, _time);
        Assert.Equal(OutageResult.Completed, outcome.Result);
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.Null(_journal.Load());
        Assert.Null(m.Current);
        var rec = Assert.Single(_log.Records);
        Assert.InRange(rec.ActualDown.TotalSeconds, 10, 10.5);
        Assert.True(rec.Recovered);
    }

    [Fact]
    public async Task Journal_is_written_before_the_link_is_disabled()
    {
        var m = Create();
        await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(1), "manual"), _time);
        Assert.NotNull(_wlan.JournalSeenOnDisable);
    }

    [Fact]
    public async Task Cancelling_the_caller_still_reenables()
    {
        var m = Create();
        using var cts = new CancellationTokenSource();
        var t = m.RunOutageAsync(TimeSpan.FromSeconds(60), "manual", cts.Token);
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(3));
        Assert.Equal(AdapterState.Down, _wlan.State);

        cts.Cancel();
        var task = Task.Run(async () => { try { await t; } catch (OperationCanceledException) { return true; } return false; });
        Assert.True(await Drive.RunAsync(task, _time));

        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public async Task Disable_failure_still_runs_recovery_and_reports_failure()
    {
        _wlan.DisableThrows = new InvalidOperationException("driver exploded");
        var m = Create();
        var outcome = await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(5), "manual"), _time);
        Assert.Equal(OutageResult.Failed, outcome.Result);
        Assert.Contains("driver exploded", outcome.Record!.Error);
        Assert.Equal(AdapterState.Up, _wlan.State);
    }

    [Fact]
    public async Task Slow_link_up_is_measured_in_TimeToUp()
    {
        _wlan.LinkUpDelay = TimeSpan.FromSeconds(1.5);
        var m = Create();
        var outcome = await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(2), "manual"), _time);
        Assert.Equal(OutageResult.Completed, outcome.Result);
        Assert.InRange(outcome.Record!.TimeToUp.TotalSeconds, 1.5, 1.8);
        Assert.Equal(1, outcome.Record.EnableAttempts);
    }

    [Fact]
    public async Task Retries_enable_and_escalates_from_the_configured_attempt()
    {
        _wlan.FailEnableTimes = int.MaxValue; // plain enable never works; only escalation does
        var m = Create(Fast() with { EscalateFromAttempt = 3 });
        var outcome = await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(1), "manual"), _time);
        Assert.Equal(OutageResult.Completed, outcome.Result);
        Assert.Equal(3, outcome.Record!.EnableAttempts);
        Assert.Equal(1, _wlan.EscalateCalls);
    }

    [Fact]
    public async Task Unrecoverable_link_reports_failure_and_keeps_journal()
    {
        _wlan.NeverUp = true;
        var m = Create(Fast() with { EnableAttempts = 3 });
        var outcome = await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(1), "manual"), _time);
        Assert.Equal(OutageResult.Failed, outcome.Result);
        Assert.False(outcome.Record!.Recovered);
        Assert.NotNull(_journal.Load());
        Assert.Equal(3, _wlan.EnableCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(901)]
    public async Task Down_time_outside_the_safety_cap_is_rejected(int seconds)
    {
        var m = Create();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => m.RunOutageAsync(TimeSpan.FromSeconds(seconds), "manual"));
        Assert.Equal(0, _wlan.DisableCalls);
    }

    [Fact]
    public async Task Overlapping_request_is_rejected_by_default()
    {
        var m = Create();
        var first = m.RunOutageAsync(TimeSpan.FromSeconds(10), "manual");
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(2));
        var second = await m.RunOutageAsync(TimeSpan.FromSeconds(10), "manual");
        Assert.Equal(OutageResult.RejectedBusy, second.Result);
        await Drive.RunAsync(first, _time);
        Assert.Equal(1, _wlan.DisableCalls);
    }

    [Fact]
    public async Task Overlapping_request_extends_the_deadline_but_respects_the_total_cap()
    {
        var m = Create(Fast() with { Overlap = OverlapPolicy.Extend, MaxDown = TimeSpan.FromSeconds(30) });
        var first = m.RunOutageAsync(TimeSpan.FromSeconds(10), "manual");
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(8));

        var ext = await m.RunOutageAsync(TimeSpan.FromSeconds(25), "manual"); // wants until t=33, cap is t=30
        Assert.Equal(OutageResult.Extended, ext.Result);

        var outcome = await Drive.RunAsync(first, _time);
        Assert.Equal(OutageResult.Completed, outcome.Result);
        Assert.InRange(outcome.Record!.ActualDown.TotalSeconds, 30, 30.5);
        Assert.Equal(1, _wlan.DisableCalls);
    }

    [Fact]
    public async Task Recovery_with_expired_journal_reenables_immediately()
    {
        _wlan.State = AdapterState.Down;
        var now = _time.GetUtcNow();
        _journal.Save(new PendingOutage("Wi-Fi", now - TimeSpan.FromSeconds(30), now - TimeSpan.FromSeconds(20), "manual"));
        var m = Create();

        Assert.True(await Drive.RunAsync(m.RecoverAsync(), _time));
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.Null(_journal.Load());
        Assert.Equal(0, _wlan.DisableCalls);
    }

    [Fact]
    public async Task Recovery_waits_out_the_remaining_time()
    {
        _wlan.State = AdapterState.Down;
        var now = _time.GetUtcNow();
        _journal.Save(new PendingOutage("Wi-Fi", now, now + TimeSpan.FromSeconds(8), "manual"));
        var m = Create();

        var t = m.RecoverAsync();
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(4));
        Assert.Equal(AdapterState.Down, _wlan.State);
        await Drive.RunAsync(t, _time);
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.Equal("recovery", Assert.Single(_log.Records).Source);
    }

    [Fact]
    public async Task Recovery_clamps_an_absurd_journal_deadline_to_MaxDown()
    {
        _wlan.State = AdapterState.Down;
        var now = _time.GetUtcNow();
        _journal.Save(new PendingOutage("Wi-Fi", now, now + TimeSpan.FromDays(3), "manual"));
        var m = Create(Fast() with { MaxDown = TimeSpan.FromMinutes(2) });

        await Drive.RunAsync(m.RecoverAsync(), _time);
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.True((_time.GetUtcNow() - now) <= TimeSpan.FromSeconds(125));
    }

    [Fact]
    public async Task Recovery_without_journal_does_nothing()
    {
        var m = Create();
        Assert.False(await m.RecoverAsync());
        Assert.Equal(0, _wlan.EnableCalls);
    }

    [Fact]
    public async Task RestoreNow_ends_an_active_outage_early()
    {
        var m = Create();
        var t = m.RunOutageAsync(TimeSpan.FromSeconds(100), "manual");
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(3));
        Assert.Equal(AdapterState.Down, _wlan.State);

        var restore = await Drive.RunAsync(m.RestoreNowAsync(), _time);
        Assert.Equal(OutageResult.Completed, restore.Result);
        Assert.Equal(AdapterState.Up, _wlan.State);
        var outcome = await Drive.RunAsync(t, _time);
        Assert.InRange(outcome.Record!.ActualDown.TotalSeconds, 3, 4);
    }

    [Fact]
    public async Task RestoreNow_without_active_outage_reenables_a_stuck_adapter_and_clears_journal()
    {
        _wlan.State = AdapterState.Down;
        var now = _time.GetUtcNow();
        _journal.Save(new PendingOutage("Wi-Fi", now, now + TimeSpan.FromSeconds(5), "manual"));
        var m = Create();

        var r = await Drive.RunAsync(m.RestoreNowAsync(), _time);
        Assert.Equal(OutageResult.Completed, r.Result);
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.Null(_journal.Load());
        Assert.Equal(1, _guard.Disarmed); // this is what the SYSTEM backstop task relies on to clean itself up
    }

    [Fact]
    public async Task Backstop_is_armed_before_disable_and_disarmed_after_recovery()
    {
        var m = Create();
        var t0 = _time.GetUtcNow();
        _guard.OnArm = () => Assert.Equal(AdapterState.Up, _wlan.State); // still up when armed
        await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(10), "manual"), _time);

        Assert.Equal(t0 + TimeSpan.FromSeconds(10), Assert.Single(_guard.Armed));
        Assert.Equal(1, _guard.Disarmed);
    }

    [Fact]
    public async Task Backstop_stays_armed_when_the_link_cannot_be_recovered()
    {
        _wlan.NeverUp = true;
        var m = Create(Fast() with { EnableAttempts = 1 });
        await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(1), "manual"), _time);
        Assert.Equal(0, _guard.Disarmed);
    }

    [Fact]
    public async Task Backstop_is_rearmed_on_extension()
    {
        var m = Create(Fast() with { Overlap = OverlapPolicy.Extend });
        var first = m.RunOutageAsync(TimeSpan.FromSeconds(10), "manual");
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(5));
        await m.RunOutageAsync(TimeSpan.FromSeconds(10), "manual");
        await Drive.RunAsync(first, _time);
        Assert.Equal(2, _guard.Armed.Count);
        Assert.True(_guard.Armed[1] > _guard.Armed[0]);
    }

    [Fact]
    public async Task Failing_to_arm_the_backstop_does_not_block_the_outage_but_is_logged()
    {
        _guard.ArmThrows = new InvalidOperationException("no powershell");
        var m = Create();
        var outcome = await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(2), "manual"), _time);
        Assert.Equal(OutageResult.Completed, outcome.Result);
        Assert.Contains("no powershell", outcome.Record!.Error);
        Assert.Equal(1, _wlan.DisableCalls);
    }

    [Fact]
    public async Task Changed_event_fires_when_an_outage_starts_and_ends()
    {
        var m = Create();
        var events = 0;
        m.Changed += () => Interlocked.Increment(ref events);
        await Drive.RunAsync(m.RunOutageAsync(TimeSpan.FromSeconds(2), "manual"), _time);
        Assert.True(events >= 2);
    }
}
