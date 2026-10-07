namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="SearchGuidance"/>.</summary>
public sealed class SearchGuidanceTests
{
    private static SearchResultHit Hit(double score) => new("p", 0, score, null, null, null, null);

    /// <summary>Verifies the best score and gap come from the first two hits.</summary>
    [Fact]
    public void Summarize_TwoHits_ReturnsBestAndGap()
    {
        var (best, gap) = SearchGuidance.Summarize([Hit(0.81), Hit(0.62), Hit(0.3)]);

        Assert.Equal(0.81, best);
        Assert.Equal(0.19, gap);
    }

    /// <summary>Verifies no gap is reported without a runner-up.</summary>
    [Fact]
    public void Summarize_SingleOrNoHit_HasNoGap()
    {
        Assert.Null(SearchGuidance.Summarize([Hit(0.9)]).Gap);
        Assert.Equal((0, (double?)null), SearchGuidance.Summarize([]));
    }

    /// <summary>Verifies the hint appears only for a decisive lead.</summary>
    [Fact]
    public void Hint_OnlyWhenTopHitLeads()
    {
        Assert.NotNull(SearchGuidance.Hint(0.12));
        Assert.Null(SearchGuidance.Hint(0.03));
        Assert.Null(SearchGuidance.Hint(null));
    }
}
