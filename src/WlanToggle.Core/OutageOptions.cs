namespace WlanToggle.Core;

public enum OverlapPolicy
{
    /// <summary>A request that arrives during an outage is rejected.</summary>
    Ignore,
    /// <summary>A request during an outage pushes the re-enable deadline out (still bounded by <see cref="OutageOptions.MaxDown"/> in total).</summary>
    Extend,
}

public sealed record OutageOptions
{
    /// <summary>Hard upper bound on how long the link may be down for one outage (safety net).</summary>
    public TimeSpan MaxDown { get; init; } = TimeSpan.FromSeconds(120);

    public OverlapPolicy Overlap { get; init; } = OverlapPolicy.Ignore;

    /// <summary>How many times to try bringing the link back before giving up (journal stays set).</summary>
    public int EnableAttempts { get; init; } = 5;

    /// <summary>From this attempt onwards <see cref="IWlanController.EscalateAsync"/> is used after the plain enable.</summary>
    public int EscalateFromAttempt { get; init; } = 3;

    /// <summary>How long to wait for the link to report Up after each enable attempt.</summary>
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan VerifyPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Pause between failed enable attempts.</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(2);
}
