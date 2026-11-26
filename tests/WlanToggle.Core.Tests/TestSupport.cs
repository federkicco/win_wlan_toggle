using Microsoft.Extensions.Time.Testing;
using WlanToggle.Core;

namespace WlanToggle.Core.Tests;

public sealed class FakeWlan(FakeTimeProvider time, IJournalStore? journal = null) : IWlanController
{
    private DateTimeOffset? _upAt;
    private AdapterState _state = AdapterState.Up;

    public string AdapterName => "Wi-Fi";
    public int EnableCalls, DisableCalls, EscalateCalls;

    /// <summary>The next N EnableAsync calls have no effect.</summary>
    public int FailEnableTimes;
    /// <summary>After a working enable, the link reports Up only after this delay.</summary>
    public TimeSpan LinkUpDelay;
    /// <summary>Even EscalateAsync cannot bring the link up.</summary>
    public bool NeverUp;
    public Exception? DisableThrows;
    public PendingOutage? JournalSeenOnDisable;

    public AdapterState State
    {
        get
        {
            if (_state == AdapterState.Down && _upAt is { } t && time.GetUtcNow() >= t) _state = AdapterState.Up;
            return _state;
        }
        set { _state = value; _upAt = null; }
    }

    public Task<AdapterState> GetStateAsync(CancellationToken ct) => Task.FromResult(State);

    public Task DisableAsync(CancellationToken ct)
    {
        DisableCalls++;
        JournalSeenOnDisable = journal?.Load();
        if (DisableThrows is { } ex) throw ex;
        State = AdapterState.Down;
        return Task.CompletedTask;
    }

    public Task EnableAsync(CancellationToken ct)
    {
        EnableCalls++;
        if (FailEnableTimes > 0) { FailEnableTimes--; return Task.CompletedTask; }
        if (!NeverUp) _upAt = time.GetUtcNow() + LinkUpDelay;
        return Task.CompletedTask;
    }

    public Task EscalateAsync(CancellationToken ct)
    {
        EscalateCalls++;
        if (!NeverUp) _upAt = time.GetUtcNow();
        return Task.CompletedTask;
    }
}

public sealed class ListSink : IOutageLogSink
{
    public List<OutageRecord> Records { get; } = [];
    public void Write(OutageRecord record) => Records.Add(record);
}

public static class Drive
{
    /// <summary>Advance fake time in small steps until the task completes.</summary>
    public static async Task<T> RunAsync<T>(Task<T> task, FakeTimeProvider time, int stepMs = 100, int maxSteps = 200_000)
    {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(stepMs));
            await Task.Delay(1);
        }
        Assert.True(task.IsCompleted, "task did not complete within the step budget");
        return await task;
    }

    public static async Task RunAsync(Task task, FakeTimeProvider time, int stepMs = 100, int maxSteps = 200_000)
    {
        await RunAsync(task.ContinueWith(_ => 0, TaskScheduler.Default), time, stepMs, maxSteps);
        await task;
    }

    /// <summary>Advance fake time by a fixed amount, letting continuations run.</summary>
    public static async Task AdvanceAsync(FakeTimeProvider time, TimeSpan total, int stepMs = 100)
    {
        for (var elapsed = 0; elapsed < total.TotalMilliseconds; elapsed += stepMs)
        {
            time.Advance(TimeSpan.FromMilliseconds(stepMs));
            await Task.Delay(1);
        }
    }
}
