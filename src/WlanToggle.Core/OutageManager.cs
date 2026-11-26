using System.Runtime.ExceptionServices;

namespace WlanToggle.Core;

public enum OutageResult
{
    /// <summary>Link went down, came back, and was verified up.</summary>
    Completed,
    /// <summary>An outage was already running; its deadline was extended and this call returned immediately.</summary>
    Extended,
    /// <summary>An outage was already running and the overlap policy rejected this request.</summary>
    RejectedBusy,
    /// <summary>The link could not be verified up again. The journal is kept so recovery can retry.</summary>
    Failed,
}

public sealed record OutageOutcome(OutageResult Result, OutageRecord? Record);

public sealed record OutageSnapshot(DateTimeOffset DownAtUtc, DateTimeOffset ReenableAtUtc, string Source);

/// <summary>
/// Owns the "take the link down, then ALWAYS bring it back" state machine.
/// Safety properties:
///  * the journal is written before the link is disabled and cleared only once the link is verified up;
///  * re-enabling never uses the caller's cancellation token (cancelling shortens the outage, never strands it);
///  * total outage length is capped by <see cref="OutageOptions.MaxDown"/>, even across extensions and recovery.
/// </summary>
public sealed class OutageManager
{
    private readonly IWlanController _controller;
    private readonly IJournalStore _journal;
    private readonly TimeProvider _time;
    private readonly OutageOptions _opt;
    private readonly IOutageLogSink _log;
    private readonly IOutageGuard _guard;
    private readonly object _gate = new();
    private Active? _active;

    public OutageManager(IWlanController controller, IJournalStore journal, TimeProvider time,
        OutageOptions? options = null, IOutageLogSink? log = null, IOutageGuard? guard = null)
    {
        _controller = controller;
        _journal = journal;
        _time = time;
        _opt = options ?? new OutageOptions();
        _log = log ?? new NullOutageLogSink();
        _guard = guard ?? new NullOutageGuard();
    }

    /// <summary>Raised (on an arbitrary thread) when an outage starts, is extended or ends.</summary>
    public event Action? Changed;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* UI handlers must never break the safety path */ }
    }

    public OutageOptions Options => _opt;

    /// <summary>The outage currently in progress, if any.</summary>
    public OutageSnapshot? Current
    {
        get
        {
            lock (_gate)
                return _active is { } a ? new OutageSnapshot(a.DownAt, a.Deadline, a.Source) : null;
        }
    }

    /// <summary>Take the link down for <paramref name="down"/> and bring it back.</summary>
    public async Task<OutageOutcome> RunOutageAsync(TimeSpan down, string source, CancellationToken ct = default)
    {
        if (down <= TimeSpan.Zero || down > _opt.MaxDown)
            throw new ArgumentOutOfRangeException(nameof(down), down,
                $"Down time must be > 0 and <= MaxDown ({_opt.MaxDown}).");

        Active? a = null;
        var extended = false;
        DateTimeOffset? extendedTo = null;
        lock (_gate)
        {
            if (_active is { } running)
            {
                if (_opt.Overlap == OverlapPolicy.Ignore)
                    return new OutageOutcome(OutageResult.RejectedBusy, null);

                var now = _time.GetUtcNow();
                var cap = running.DownAt + _opt.MaxDown;
                var newDeadline = Max(running.Deadline, now + down);
                if (newDeadline > cap) newDeadline = cap;
                if (newDeadline > running.Deadline)
                {
                    running.Deadline = newDeadline;
                    _journal.Save(new PendingOutage(_controller.AdapterName, running.DownAt, newDeadline, running.Source));
                    running.WakeWaiter();
                    extendedTo = newDeadline;
                }
                extended = true;
            }
            else
            {
                var start = _time.GetUtcNow();
                a = _active = new Active(start, start + down, source, requested: down);
            }
        }

        if (extended)
        {
            if (extendedTo is { } to) await TryArmAsync(to).ConfigureAwait(false);
            RaiseChanged();
            return new OutageOutcome(OutageResult.Extended, null);
        }

        return await ExecuteAsync(a!, disable: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Call at process start (and after resume from sleep): if a previous run left a pending outage,
    /// finish it (wait out the remaining time, capped by MaxDown) and re-enable. Returns true if recovery ran.
    /// </summary>
    public async Task<bool> RecoverAsync(CancellationToken ct = default)
    {
        var pending = _journal.Load();
        if (pending is null) return false;

        Active a;
        lock (_gate)
        {
            if (_active is not null) return false;
            var now = _time.GetUtcNow();
            var deadline = pending.ReenableAtUtc;
            // Never trust a journal enough to stay down longer than the hard cap from *now*.
            if (deadline > now + _opt.MaxDown) deadline = now + _opt.MaxDown;
            a = _active = new Active(pending.DisabledAtUtc, deadline, "recovery",
                requested: pending.ReenableAtUtc - pending.DisabledAtUtc);
        }

        await ExecuteAsync(a, disable: false, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// "Panic button" / shutdown hook: end any outage now and make sure the link is up.
    /// Also works when no outage is active (re-enables and clears a stale journal).
    /// </summary>
    public async Task<OutageOutcome> RestoreNowAsync()
    {
        Active? a;
        lock (_gate)
        {
            a = _active;
            if (a is not null)
            {
                a.Deadline = _time.GetUtcNow();
                a.WakeWaiter();
            }
        }

        if (a is not null)
            return await a.Done.Task.ConfigureAwait(false);

        var started = _time.GetUtcNow();
        var r = await ReenableVerifiedAsync().ConfigureAwait(false);
        if (r.Recovered) _journal.Clear();
        var record = new OutageRecord("restore", _controller.AdapterName, started, r.UpAtUtc, TimeSpan.Zero,
            TimeSpan.Zero, r.TimeToUp, r.Attempts, r.Recovered, r.Error);
        _log.Write(record);
        if (r.Recovered)
        {
            // Last: if this runs inside the backstop task itself, nothing important is left to do after it.
            try { await _guard.DisarmAsync().ConfigureAwait(false); } catch { }
        }
        return new OutageOutcome(r.Recovered ? OutageResult.Completed : OutageResult.Failed, record);
    }

    private async Task<OutageOutcome> ExecuteAsync(Active a, bool disable, CancellationToken ct)
    {
        Exception? error = null;
        string? guardNote = null;
        try
        {
            // Journal first: if we die right after this, recovery knows when to re-enable.
            _journal.Save(new PendingOutage(_controller.AdapterName, a.DownAt, a.Deadline, a.Source));
            // Independent backstop (e.g. a SYSTEM scheduled task) that restores the link even if this process dies.
            guardNote = await TryArmAsync(a.Deadline).ConfigureAwait(false);
            RaiseChanged();
            if (disable) await _controller.DisableAsync(ct).ConfigureAwait(false);
            await WaitUntilDeadlineAsync(a, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex; // includes cancellation: we still must re-enable below
        }

        // From here on nothing the caller does may interrupt us.
        var r = await ReenableVerifiedAsync().ConfigureAwait(false);
        if (r.Recovered)
        {
            _journal.Clear();
            try { await _guard.DisarmAsync().ConfigureAwait(false); } catch { /* a stale backstop only re-checks the link */ }
        }

        var upAt = r.UpAtUtc;
        var errors = new List<string>();
        if (!r.Recovered) errors.Add(r.Error ?? error?.Message ?? "link did not recover");
        else if (error is not null and not OperationCanceledException) errors.Add(error.Message);
        if (guardNote is not null) errors.Add(guardNote);
        var record = new OutageRecord(a.Source, _controller.AdapterName, a.DownAt, upAt, a.Requested,
            upAt - a.DownAt, r.TimeToUp, r.Attempts, r.Recovered, errors.Count == 0 ? null : string.Join("; ", errors));
        _log.Write(record);

        var ok = r.Recovered && (error is null || error is OperationCanceledException);
        var outcome = new OutageOutcome(ok ? OutageResult.Completed : OutageResult.Failed, record);

        lock (_gate) _active = null;
        a.Done.TrySetResult(outcome);
        RaiseChanged();

        if (error is OperationCanceledException) ExceptionDispatchInfo.Capture(error).Throw();
        return outcome;
    }

    /// <summary>Arms the backstop; a failure is reported in the log but never blocks the outage. Returns a note on failure.</summary>
    private async Task<string?> TryArmAsync(DateTimeOffset latestReenableUtc)
    {
        try
        {
            await _guard.ArmAsync(latestReenableUtc, _controller.AdapterName).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return "backstop not armed: " + ex.Message;
        }
    }

    private async Task WaitUntilDeadlineAsync(Active a, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan remaining;
            CancellationToken wake;
            lock (_gate)
            {
                remaining = a.Deadline - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) return;
                wake = a.NewWaiter();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, wake);
            try
            {
                await Task.Delay(remaining, _time, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // woken because the deadline moved: re-evaluate
            }
        }
    }

    private readonly record struct ReenableResult(bool Recovered, int Attempts, DateTimeOffset UpAtUtc, TimeSpan TimeToUp, string? Error);

    private async Task<ReenableResult> ReenableVerifiedAsync()
    {
        var ct = CancellationToken.None;
        var began = _time.GetUtcNow();
        string? lastError = null;

        if (await SafeStateAsync().ConfigureAwait(false) == AdapterState.Up)
            return new ReenableResult(true, 0, _time.GetUtcNow(), TimeSpan.Zero, null);

        for (var attempt = 1; attempt <= _opt.EnableAttempts; attempt++)
        {
            try
            {
                await _controller.EnableAsync(ct).ConfigureAwait(false);
                if (attempt >= _opt.EscalateFromAttempt)
                    await _controller.EscalateAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            var giveUpAt = _time.GetUtcNow() + _opt.VerifyTimeout;
            while (true)
            {
                if (await SafeStateAsync().ConfigureAwait(false) == AdapterState.Up)
                {
                    var now = _time.GetUtcNow();
                    return new ReenableResult(true, attempt, now, now - began, lastError);
                }
                if (_time.GetUtcNow() >= giveUpAt) break;
                await Task.Delay(_opt.VerifyPollInterval, _time, ct).ConfigureAwait(false);
            }

            lastError ??= $"attempt {attempt}: link not up after {_opt.VerifyTimeout.TotalSeconds:F0}s";
            if (attempt < _opt.EnableAttempts)
                await Task.Delay(_opt.RetryBackoff, _time, ct).ConfigureAwait(false);
        }

        var end = _time.GetUtcNow();
        return new ReenableResult(false, _opt.EnableAttempts, end, end - began, lastError);
    }

    private async Task<AdapterState> SafeStateAsync()
    {
        try { return await _controller.GetStateAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { return AdapterState.Unknown; }
    }

    private static DateTimeOffset Max(DateTimeOffset x, DateTimeOffset y) => x > y ? x : y;

    private sealed class Active(DateTimeOffset downAt, DateTimeOffset deadline, string source, TimeSpan requested)
    {
        private CancellationTokenSource? _waiter;
        public DateTimeOffset DownAt { get; } = downAt;
        public DateTimeOffset Deadline { get; set; } = deadline;
        public string Source { get; } = source;
        public TimeSpan Requested { get; } = requested;
        public TaskCompletionSource<OutageOutcome> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Call under the manager's lock.</summary>
        public CancellationToken NewWaiter()
        {
            _waiter = new CancellationTokenSource();
            return _waiter.Token;
        }

        /// <summary>Call under the manager's lock.</summary>
        public void WakeWaiter() => _waiter?.Cancel();
    }
}
