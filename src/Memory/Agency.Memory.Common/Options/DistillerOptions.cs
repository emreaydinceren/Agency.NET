namespace Agency.Memory.Common.Options;

/// <summary>
/// Configuration for the distiller background service.
/// </summary>
public sealed class DistillerOptions
{
    /// <summary>Gets or sets the inactivity timeout before an auto-distillation is triggered (default: 5 minutes).</summary>
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the maximum number of retry attempts on transient failure (default: 3).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Gets or sets the base delay for exponential backoff retries (default: 2 seconds).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets or sets the maximum number of pending distillation jobs per session (default: 32).</summary>
    public int PerSessionQueueCapacity { get; set; } = 32;

    /// <summary>Gets or sets the backpressure policy when the per-session queue is full (default: <see cref="BackpressurePolicy.DropOldest"/>).</summary>
    public BackpressurePolicy Backpressure { get; set; } = BackpressurePolicy.DropOldest;
}
