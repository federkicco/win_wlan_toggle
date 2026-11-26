using Microsoft.Extensions.Time.Testing;
using WlanToggle.Core;

namespace WlanToggle.Core.Tests;

public class PeriodicSchedulerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryJournalStore _journal = new();
    private readonly ListSink _log = new();
    private readonly FakeWlan _wlan;
    private readonly OutageManager _mgr;
    private readonly PeriodicScheduler _sched;

    public PeriodicSchedulerTests()
    {
        _wlan = new FakeWlan(_time, _journal);
        _mgr = new OutageManager(_wlan, _journal, _time, new OutageOptions
        {
            VerifyTimeout = TimeSpan.FromSeconds(2),
            VerifyPollInterval = TimeSpan.FromMilliseconds(100),
            RetryBackoff = TimeSpan.FromMilliseconds(200),
            EnableAttempts = 2,
        }, _log);
        _sched = new PeriodicScheduler(_mgr, _time, () => 0.5);
    }

    [Fact]
    public async Task Cycles_start_on_a_fixed_grid_without_drift()
    {
        var t0 = _time.GetUtcNow();
        var opt = new PeriodicOptions { Interval = TimeSpan.FromSeconds(60), Down = TimeSpan.FromSeconds(10), MaxCycles = 3 };
        var result = await Drive.RunAsync(_sched.RunAsync(opt, CancellationToken.None), _time);

        Assert.Equal(new SchedulerResult(3, SchedulerStopReason.MaxCycles), result);
        Assert.Equal(3, _log.Records.Count);
        for (var i = 0; i < 3; i++)
            Assert.InRange((_log.Records[i].DownAtUtc - t0).TotalSeconds, i * 60, i * 60 + 0.5);
        Assert.All(_log.Records, r => Assert.InRange(r.ActualDown.TotalSeconds, 10, 10.5));
    }

    [Fact]
    public async Task StartDelay_postpones_the_first_cycle()
    {
        var t0 = _time.GetUtcNow();
        var opt = new PeriodicOptions { Interval = TimeSpan.FromSeconds(60), Down = TimeSpan.FromSeconds(5), StartDelay = TimeSpan.FromSeconds(20), MaxCycles = 1 };
        await Drive.RunAsync(_sched.RunAsync(opt, CancellationToken.None), _time);
        Assert.InRange((_log.Records[0].DownAtUtc - t0).TotalSeconds, 20, 20.5);
    }

    [Fact]
    public async Task Jitter_is_applied_from_the_injected_random_source()
    {
        var t0 = _time.GetUtcNow();
        var opt = new PeriodicOptions
        {
            Interval = TimeSpan.FromSeconds(60), Down = TimeSpan.FromSeconds(10),
            IntervalJitter = TimeSpan.FromSeconds(10), DownJitter = TimeSpan.FromSeconds(4), MaxCycles = 1,
        };
        await Drive.RunAsync(_sched.RunAsync(opt, CancellationToken.None), _time);
        var rec = _log.Records[0];
        Assert.InRange((rec.DownAtUtc - t0).TotalSeconds, 5, 5.5);   // 0.5 * 10 s
        Assert.InRange(rec.RequestedDown.TotalSeconds, 11.9, 12.1);  // 10 + 0.5 * 4
    }

    [Fact]
    public async Task MaxDuration_stops_starting_new_cycles()
    {
        var opt = new PeriodicOptions { Interval = TimeSpan.FromSeconds(30), Down = TimeSpan.FromSeconds(5), MaxDuration = TimeSpan.FromSeconds(100) };
        var result = await Drive.RunAsync(_sched.RunAsync(opt, CancellationToken.None), _time);
        Assert.Equal(SchedulerStopReason.MaxDuration, result.Reason);
        Assert.Equal(4, result.CyclesRun); // slots at 0, 30, 60, 90
    }

    [Fact]
    public async Task Cancelling_mid_outage_reenables_and_reports_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        var opt = new PeriodicOptions { Interval = TimeSpan.FromSeconds(60), Down = TimeSpan.FromSeconds(30) };
        var t = _sched.RunAsync(opt, cts.Token);
        await Drive.AdvanceAsync(_time, TimeSpan.FromSeconds(10));
        Assert.Equal(AdapterState.Down, _wlan.State);

        cts.Cancel();
        var result = await Drive.RunAsync(t, _time);
        Assert.Equal(SchedulerStopReason.Cancelled, result.Reason);
        Assert.Equal(AdapterState.Up, _wlan.State);
        Assert.Null(_journal.Load());
    }

    [Fact]
    public async Task Stops_instead_of_looping_when_the_link_cannot_be_recovered()
    {
        _wlan.NeverUp = true;
        var opt = new PeriodicOptions { Interval = TimeSpan.FromSeconds(60), Down = TimeSpan.FromSeconds(5) };
        var result = await Drive.RunAsync(_sched.RunAsync(opt, CancellationToken.None), _time);
        Assert.Equal(SchedulerStopReason.Failure, result.Reason);
        Assert.Equal(1, _wlan.DisableCalls);
    }

    [Theory]
    [InlineData(60, 60)]   // down == interval
    [InlineData(60, 120)]  // exceeds the safety cap
    [InlineData(0, 10)]    // zero interval
    public void Invalid_options_are_rejected(int intervalS, int downS)
    {
        var opt = new PeriodicOptions { Interval = TimeSpan.FromSeconds(intervalS), Down = TimeSpan.FromSeconds(downS) };
        var cap = downS == 120 ? TimeSpan.FromSeconds(100) : TimeSpan.FromSeconds(120);
        Assert.Throws<ArgumentException>(() => opt.Validate(cap));
    }
}
