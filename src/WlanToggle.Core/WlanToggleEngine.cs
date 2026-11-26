namespace WlanToggle.Core;

public readonly record struct SetResult(bool Ok, string? Error)
{
    public static readonly SetResult Success = new(true, null);
    public static SetResult Fail(string error) => new(false, error);
}

public sealed record EngineStatus(
    bool Disconnected,
    DateTimeOffset? ReconnectAtUtc,
    bool PeriodicEnabled,
    DateTimeOffset? NextPeriodicUtc,
    string? LastError);

/// <summary>
/// Everything the UI needs: current settings, on-demand toggle, periodic schedule, and the safety hooks.
/// The UI layer should contain no logic beyond calling this and rendering <see cref="Status"/>.
/// </summary>
public sealed class WlanToggleEngine
{
    private readonly Func<DisconnectMethod, IWlanController> _controllerFactory;
    private readonly IJournalStore _journal;
    private readonly TimeProvider _time;
    private readonly IOutageLogSink _log;
    private readonly IOutageGuard _guard;
    private readonly Action<WlanToggleConfig>? _persist;
    private readonly Func<double>? _random;
    private readonly SemaphoreSlim _cfgLock = new(1, 1);
    private readonly object _sync = new();

    private WlanToggleConfig _config;
    private OutageManager _manager;
    private PeriodicScheduler? _scheduler;
    private CancellationTokenSource? _periodicCts;
    private Task? _periodicTask;
    private string? _lastError;

    public WlanToggleEngine(WlanToggleConfig config, Func<DisconnectMethod, IWlanController> controllerFactory,
        IJournalStore journal, TimeProvider time, IOutageLogSink? log = null, IOutageGuard? guard = null,
        Action<WlanToggleConfig>? persist = null, Func<double>? random = null)
    {
        config.Validate();
        _config = config;
        _controllerFactory = controllerFactory;
        _journal = journal;
        _time = time;
        _log = log ?? new NullOutageLogSink();
        _guard = guard ?? new NullOutageGuard();
        _persist = persist;
        _random = random;
        _manager = CreateManager(config);
    }

    public WlanToggleConfig Config => _config;

    /// <summary>Raised on an arbitrary thread whenever <see cref="Status"/> may have changed.</summary>
    public event Action? Changed;

    public EngineStatus Status
    {
        get
        {
            var cur = _manager.Current;
            return new EngineStatus(cur is not null, cur?.ReenableAtUtc, _config.PeriodicEnabled,
                _config.PeriodicEnabled ? _scheduler?.NextRunUtc : null, _lastError);
        }
    }

    /// <summary>Finish any outage a previous run left behind, then resume the periodic schedule if enabled.</summary>
    public async Task StartAsync()
    {
        try { await _manager.RecoverAsync().ConfigureAwait(false); }
        catch (Exception ex) { SetError("recovery failed: " + ex.Message); }
        await _cfgLock.WaitAsync().ConfigureAwait(false);
        try { if (_config.PeriodicEnabled) StartPeriodic(); }
        finally { _cfgLock.Release(); }
        RaiseChanged();
    }

    /// <summary>Disconnect now and reconnect after <see cref="WlanToggleConfig.ReconnectAfter"/>.</summary>
    public async Task<OutageOutcome> ToggleNowAsync(string source = "manual")
    {
        OutageManager mgr; TimeSpan down;
        lock (_sync) { mgr = _manager; down = _config.ReconnectAfter; }
        try
        {
            var outcome = await mgr.RunOutageAsync(down, source).ConfigureAwait(false);
            if (outcome.Result == OutageResult.Failed) SetError(outcome.Record?.Error ?? "link did not recover");
            else if (outcome.Result == OutageResult.Completed) SetError(null);
            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetError(ex.Message);
            throw;
        }
    }

    public Task<SetResult> SetMethodAsync(DisconnectMethod method) => ChangeAsync(
        c => c with { Method = method }, rebuildManager: method != _config.Method, restartPeriodic: true);

    public Task<SetResult> SetReconnectAfterAsync(TimeSpan value) => ChangeAsync(
        c => c with { ReconnectAfter = value }, rebuildManager: false, restartPeriodic: true);

    public Task<SetResult> SetPeriodicIntervalAsync(TimeSpan value) => ChangeAsync(
        c => c with { Periodic = c.Periodic with { Interval = value } }, rebuildManager: false, restartPeriodic: true);

    public Task<SetResult> SetPeriodicEnabledAsync(bool enabled) => ChangeAsync(
        c => c with { PeriodicEnabled = enabled }, rebuildManager: false, restartPeriodic: true);

    /// <summary>Panic button: end any outage now and make sure the link is up.</summary>
    public async Task<OutageOutcome> RestoreNowAsync()
    {
        var o = await _manager.RestoreNowAsync().ConfigureAwait(false);
        SetError(o.Result == OutageResult.Failed ? o.Record?.Error ?? "link did not recover" : null);
        return o;
    }

    /// <summary>Stop the schedule and restore the link (call on exit / logoff / shutdown).</summary>
    public async Task ShutdownAsync()
    {
        await _cfgLock.WaitAsync().ConfigureAwait(false);
        try { await StopPeriodicAsync().ConfigureAwait(false); }
        finally { _cfgLock.Release(); }
        await _manager.RestoreNowAsync().ConfigureAwait(false);
    }

    private async Task<SetResult> ChangeAsync(Func<WlanToggleConfig, WlanToggleConfig> change, bool rebuildManager, bool restartPeriodic)
    {
        await _cfgLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var candidate = change(_config);
            try { candidate.Validate(); }
            catch (ArgumentException ex) { return SetResult.Fail(ex.Message); }

            if (candidate == _config) return SetResult.Success;

            if (rebuildManager && _manager.Current is not null)
                return SetResult.Fail("Cannot change the disconnect mode while the link is down.");

            await StopPeriodicAsync().ConfigureAwait(false);
            lock (_sync)
            {
                _config = candidate;
                if (rebuildManager)
                {
                    _manager.Changed -= RaiseChanged;
                    _manager = CreateManager(candidate);
                }
            }

            try { _persist?.Invoke(candidate); }
            catch (Exception ex) { SetError("could not save settings: " + ex.Message); }

            if (restartPeriodic && candidate.PeriodicEnabled) StartPeriodic();
            RaiseChanged();
            return SetResult.Success;
        }
        finally { _cfgLock.Release(); }
    }

    private OutageManager CreateManager(WlanToggleConfig cfg)
    {
        var m = new OutageManager(_controllerFactory(cfg.Method), _journal, _time, cfg.Outage, _log, _guard);
        m.Changed += RaiseChanged;
        return m;
    }

    // Callers hold _cfgLock.
    private void StartPeriodic()
    {
        var opts = _config.EffectivePeriodic with { StartDelay = _config.Periodic.Interval }; // first cycle after one interval
        var scheduler = new PeriodicScheduler(_manager, _time, _random);
        var cts = new CancellationTokenSource();
        _scheduler = scheduler;
        _periodicCts = cts;
        _periodicTask = Task.Run(async () =>
        {
            try
            {
                var result = await scheduler.RunAsync(opts, cts.Token).ConfigureAwait(false);
                if (result.Reason != SchedulerStopReason.Cancelled)
                    await EndPeriodicAsync(scheduler, cts.Token,
                        result.Reason == SchedulerStopReason.Failure ? "periodic schedule stopped: the link did not come back" : null)
                        .ConfigureAwait(false);
            }
            catch (Exception ex) { SetError("periodic schedule stopped: " + ex.Message); }
        });
    }

    /// <summary>The schedule ended on its own (failure or configured limit): switch it off and persist that.</summary>
    private async Task EndPeriodicAsync(PeriodicScheduler finished, CancellationToken stopToken, string? error)
    {
        // stopToken is cancelled by StopPeriodicAsync, which holds _cfgLock while waiting for us: don't deadlock on it.
        try { await _cfgLock.WaitAsync(stopToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (!ReferenceEquals(_scheduler, finished)) return; // already replaced by a newer schedule
            if (error is not null) SetError(error);
            lock (_sync) _config = _config with { PeriodicEnabled = false };
            try { _persist?.Invoke(_config); } catch { }
            _scheduler = null; _periodicCts = null; _periodicTask = null;
        }
        finally { _cfgLock.Release(); }
        RaiseChanged();
    }

    // Callers hold _cfgLock.
    private async Task StopPeriodicAsync()
    {
        var cts = _periodicCts; var task = _periodicTask;
        _periodicCts = null; _periodicTask = null; _scheduler = null;
        if (cts is null) return;
        cts.Cancel();
        if (task is not null)
        {
            // Cancelling mid-outage re-enables the link first; the scheduler task finishes afterwards.
            try { await task.WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false); } catch { }
        }
        cts.Dispose();
    }

    private void SetError(string? error)
    {
        _lastError = error;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
