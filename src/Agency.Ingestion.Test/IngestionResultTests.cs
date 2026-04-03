namespace Agency.Ingestion.Test;

/// <summary>
/// IngestionResult is the return value of DefaultIngestionPipeline.ExecuteAsync.
/// Callers use it to decide whether to retry, log alerts, or surface errors to
/// the user. These tests verify that the computed property and the default for
/// optional parameters work correctly so callers can rely on them without
/// defensive null checks in the happy path.
/// </summary>
public sealed class IngestionResultTests
{
    /// <summary>
    /// IsSuccess is the primary signal callers use after a pipeline run.
    /// When every chunk was upserted successfully (Failed == 0), the pipeline
    /// ran cleanly and the caller should not need to inspect FailedKeys.
    /// </summary>
    [Fact]
    public void IsSuccess_ReturnsTrue_WhenFailedIsZero()
    {
        var result = new IngestionResult(5, 0);

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Even a single failure means something was not persisted to the vector
    /// store. IsSuccess must return false so that callers who only check this
    /// flag (rather than inspecting counts) still detect the partial failure.
    /// </summary>
    [Fact]
    public void IsSuccess_ReturnsFalse_WhenFailedIsGreaterThanZero()
    {
        var result = new IngestionResult(3, 2);

        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// FailedKeys is null (not an empty list) when there are no failures.
    /// Callers can check <c>result.FailedKeys is not null</c> as a guard before
    /// iterating; an empty list would require an additional <c>.Count &gt; 0</c> check
    /// and allocates memory unnecessarily in the common success path.
    /// </summary>
    [Fact]
    public void FailedKeys_DefaultsToNull()
    {
        var result = new IngestionResult(1, 0);

        Assert.Null(result.FailedKeys);
    }

    /// <summary>
    /// Verifies that all three constructor arguments are stored correctly,
    /// including the provided FailedKeys list. Callers rely on the exact same
    /// list instance being accessible for logging or retry logic.
    /// </summary>
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var keys = new List<string> { "key1", "key2" };
        var result = new IngestionResult(3, 2, keys);

        Assert.Equal(3, result.Succeeded);
        Assert.Equal(2, result.Failed);
        Assert.Equal(keys, result.FailedKeys);
    }
}
