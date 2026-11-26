namespace WlanToggle.Core;

public sealed record PeriodicOptions
{
    /// <summary>Time between the start of one cycle and the start of the next.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the link is down in each cycle.</summary>
    public TimeSpan Down { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Each cycle starts up to this much later than its nominal slot (uniform random; does not accumulate).</summary>
    public TimeSpan IntervalJitter { get; init; } = TimeSpan.Zero;

    /// <summary>Each outage lasts up to this much longer than <see cref="Down"/> (uniform random).</summary>
    public TimeSpan DownJitter { get; init; } = TimeSpan.Zero;

    /// <summary>Delay before the first cycle.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.Zero;

    /// <summary>Stop after this many cycles (null = unlimited).</summary>
    public int? MaxCycles { get; init; }

    /// <summary>Stop starting new cycles after this much wall time (null = unlimited).</summary>
    public TimeSpan? MaxDuration { get; init; }

    public void Validate(TimeSpan maxDown)
    {
        if (Interval <= TimeSpan.Zero) throw new ArgumentException("Interval must be > 0.");
        if (Down <= TimeSpan.Zero) throw new ArgumentException("Down must be > 0.");
        if (IntervalJitter < TimeSpan.Zero || DownJitter < TimeSpan.Zero || StartDelay < TimeSpan.Zero)
            throw new ArgumentException("Jitter and StartDelay must be >= 0.");
        if (Down + DownJitter > maxDown)
            throw new ArgumentException($"Down + DownJitter exceeds the safety cap MaxDown ({maxDown}).");
        if (Down + DownJitter + IntervalJitter >= Interval)
            throw new ArgumentException("Down + jitters must be shorter than Interval, or the link would never be up between cycles.");
        if (MaxCycles is <= 0) throw new ArgumentException("MaxCycles must be > 0.");
        if (MaxDuration is { } d && d <= TimeSpan.Zero) throw new ArgumentException("MaxDuration must be > 0.");
    }
}

public enum SchedulerStopReason { Cancelled, MaxCycles, MaxDuration, Failure }

public sealed record SchedulerResult(int CyclesRun, SchedulerStopReason Reason);

/// <summary>Runs repeated outages on an absolute (drift-free) schedule.</summary>
public sealed class PeriodicScheduler
{
    private readonly OutageManager _manager;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private int _cyclesRun;
    private long _nextRunTicks = -1;

    public PeriodicScheduler(OutageManager manager, TimeProvider time, Func<double>? random = null)
    {
        _manager = manager;
        _time = time;
        _random = random ?? Random.Shared.NextDouble;
    }

    public int CyclesRun => Volatile.Read(ref _cyclesRun);

    /// <summary>When the next cycle is due (null when idle or between runs).</summary>
    public DateTimeOffset? NextRunUtc
    {
        get
        {
            var t = Interlocked.Read(ref _nextRunTicks);
            return t < 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    public async Task<SchedulerResult> RunAsync(PeriodicOptions opt, CancellationToken ct)
    {
        opt.Validate(_manager.Options.MaxDown);
        var started = _time.GetUtcNow();
        var endAt = opt.MaxDuration is { } d ? started + d : (DateTimeOffset?)null;
        var slot = started + opt.StartDelay;
        Volatile.Write(ref _cyclesRun, 0);

        try
        {
            while (true)
            {
                if (opt.MaxCycles is { } max && CyclesRun >= max)
                    return new SchedulerResult(CyclesRun, SchedulerStopReason.MaxCycles);

                var fireAt = slot + Jitter(opt.IntervalJitter);
                if (endAt is { } e && fireAt >= e)
                    return new SchedulerResult(CyclesRun, SchedulerStopReason.MaxDuration);

                Interlocked.Exchange(ref _nextRunTicks, fireAt.UtcTicks);
                var wait = fireAt - _time.GetUtcNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _nextRunTicks, -1);

                var outcome = await _manager.RunOutageAsync(opt.Down + Jitter(opt.DownJitter), "periodic", ct)
                    .ConfigureAwait(false);
                if (outcome.Result != OutageResult.RejectedBusy)
                    Interlocked.Increment(ref _cyclesRun);

                // Stop rather than keep disabling a link we could not bring back.
                if (outcome.Result == OutageResult.Failed)
                    return new SchedulerResult(CyclesRun, SchedulerStopReason.Failure);

                // Advance by whole intervals from the nominal slot, skipping any that were overrun.
                slot += opt.Interval;
                var now = _time.GetUtcNow();
                while (slot <= now) slot += opt.Interval;
            }
        }
        catch (OperationCanceledException)
        {
            return new SchedulerResult(CyclesRun, SchedulerStopReason.Cancelled);
        }
        finally
        {
            Interlocked.Exchange(ref _nextRunTicks, -1);
        }
    }

    private TimeSpan Jitter(TimeSpan max) => max <= TimeSpan.Zero ? TimeSpan.Zero : max * _random();
}
