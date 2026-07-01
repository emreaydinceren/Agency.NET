namespace Agency.Ingestion;

/// <summary>
/// Captures the outcome of a completed ingestion pipeline run.
/// </summary>
public sealed record IngestionResult(
    int Succeeded,
    int Failed,
    IReadOnlyList<string>? FailedKeys = null)
{
    /// <summary>
    /// Gets a value indicating whether the pipeline completed with no failures.
    /// </summary>
    public bool IsSuccess => Failed == 0;
}
