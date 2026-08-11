namespace Agency.Memory.Retrieval.Test;

/// <summary>
/// Unit tests for <see cref="RetrievalMemoryFramingFact"/> — validates framing fact generation
/// for memory retrieval status messages injected into the knowledge context.
/// </summary>
public sealed class RetrievalMemoryFramingFactTests
{
    /// <summary>
    /// When records are retrieved, the framing fact should explain that memories are the model's
    /// own recollection and instruct it to use them confidently without asking the user to repeat.
    /// </summary>
    [Fact]
    public void Build_WithRecords_RendersTreatAsRecollection()
    {
        var result = RetrievalMemoryFramingFact.Build(hasRecords: true);

        Assert.StartsWith(RetrievalMemoryFramingFact.Prefix, result);
        Assert.Contains("your own recollection", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never claim you cannot remember", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When no records are retrieved, the framing fact should clarify that this absence is not
    /// evidence of inability to remember, and that current-session context is captured in the background.
    /// </summary>
    [Fact]
    public void Build_WithoutRecords_RendersColdStartGuidance()
    {
        var result = RetrievalMemoryFramingFact.Build(hasRecords: false);

        Assert.StartsWith(RetrievalMemoryFramingFact.Prefix, result);
        Assert.Contains("not evidence", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background", result, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Both the records-present and no-records versions must start with the same prefix,
    /// so that filter logic can reliably strip prior facts from the knowledge context
    /// before injecting the new one.
    /// </summary>
    [Fact]
    public void Prefix_IsConsistentAcrossBuilds()
    {
        var withRecords = RetrievalMemoryFramingFact.Build(hasRecords: true);
        var withoutRecords = RetrievalMemoryFramingFact.Build(hasRecords: false);

        Assert.StartsWith(RetrievalMemoryFramingFact.Prefix, withRecords);
        Assert.StartsWith(RetrievalMemoryFramingFact.Prefix, withoutRecords);
    }

    /// <summary>
    /// Both variants of the framing fact must produce non-empty output so that the knowledge
    /// context has meaningful content to work with.
    /// </summary>
    [Fact]
    public void Build_NeverReturnsEmpty()
    {
        var withRecords = RetrievalMemoryFramingFact.Build(hasRecords: true);
        var withoutRecords = RetrievalMemoryFramingFact.Build(hasRecords: false);

        Assert.NotNull(withRecords);
        Assert.NotEmpty(withRecords);
        Assert.NotNull(withoutRecords);
        Assert.NotEmpty(withoutRecords);
    }
}
