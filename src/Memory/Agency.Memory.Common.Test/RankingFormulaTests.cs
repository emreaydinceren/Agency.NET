using Agency.Memory.Common.Ranking;
using Agency.Memory.Common.Records;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Common.Test;

/// <summary>Tests for <see cref="RankingFormula"/> and <see cref="RankingWeights"/>.</summary>
public sealed class RankingFormulaTests
{
    private static MemoryRecord MakeRecord(
        double importance,
        DateTimeOffset updatedAt,
        string? sessionId = null) =>
        MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: "u1",
            sessionId: sessionId,
            contentType: ContentType.Fact,
            domain: "D",
            key: "K",
            title: "T",
            value: "V",
            tags: [],
            importance: importance,
            createdAt: updatedAt,
            updatedAt: updatedAt);

    /// <summary>Recency should be 1.0 at age zero (exp(0) = 1).</summary>
    [Fact]
    public void Score_AtZeroAge_RecencyEqualsOne()
    {
        var now = DateTimeOffset.UtcNow;
        var record = MakeRecord(importance: 0.0, updatedAt: now, sessionId: null);
        var weights = new RankingWeights(Similarity: 0.0, Recency: 1.0, Importance: 0.0, SessionMatch: 0.0);

        double score = RankingFormula.Score(
            similarity: 0.0,
            record: record,
            currentSessionId: null,
            now: now,
            weights: weights,
            halfLifeDays: 7.0);

        Assert.True(Math.Abs(score - 1.0) < 1e-9, $"Expected recency=1 but got {score}");
    }

    /// <summary>
    /// Worked example from Spec §8.3:
    /// sim=0.8, age=2 days, importance=0.6, sessionMatch=1 → score ≈ 0.845.
    /// </summary>
    [Fact]
    public void Score_WorkedExampleFromSpec_Equals_0_845_WithinTolerance()
    {
        var now = DateTimeOffset.UtcNow;
        var updatedAt = now - TimeSpan.FromDays(2);
        var record = MakeRecord(importance: 0.6, updatedAt: updatedAt, sessionId: "sess1");

        double score = RankingFormula.Score(
            similarity: 0.8,
            record: record,
            currentSessionId: "sess1",
            now: now,
            weights: RankingWeights.Default,
            halfLifeDays: 7.0);

        Assert.True(Math.Abs(score - 0.845) < 1e-3,
            $"Expected ~0.845 but got {score}");
    }

    /// <summary>sessionMatch=1 vs sessionMatch=0 should differ by exactly wₘ (default 0.1).</summary>
    [Fact]
    public void Score_SessionMatchTrue_AddsPointOne()
    {
        var now = DateTimeOffset.UtcNow;
        var record = MakeRecord(importance: 0.5, updatedAt: now, sessionId: "sess1");
        var weights = RankingWeights.Default;

        double scoreMatch = RankingFormula.Score(
            similarity: 0.5,
            record: record,
            currentSessionId: "sess1",
            now: now,
            weights: weights,
            halfLifeDays: 7.0);

        double scoreNoMatch = RankingFormula.Score(
            similarity: 0.5,
            record: record,
            currentSessionId: "other-session",
            now: now,
            weights: weights,
            halfLifeDays: 7.0);

        double diff = scoreMatch - scoreNoMatch;
        Assert.True(Math.Abs(diff - weights.SessionMatch) < 1e-9,
            $"Expected diff={weights.SessionMatch} but got {diff}");
    }

    /// <summary>Default weights must match Spec §8.3: (0.5, 0.3, 0.2, 0.1) and halfLifeDays = 7.</summary>
    [Fact]
    public void Score_DefaultWeightsAreFromSpec()
    {
        var defaults = RankingWeights.Default;

        Assert.Equal(0.5, defaults.Similarity);
        Assert.Equal(0.3, defaults.Recency);
        Assert.Equal(0.2, defaults.Importance);
        Assert.Equal(0.1, defaults.SessionMatch);
        Assert.Equal(7.0, RankingFormula.DefaultHalfLifeDays);
    }

    /// <summary>Negative cosine similarity input must be clamped to 0.</summary>
    [Fact]
    public void Score_ClipsSimilarityToZeroOne()
    {
        var now = DateTimeOffset.UtcNow;
        var record = MakeRecord(importance: 0.0, updatedAt: now, sessionId: null);

        double scoreNegative = RankingFormula.Score(
            similarity: -0.5,
            record: record,
            currentSessionId: null,
            now: now,
            weights: new RankingWeights(Similarity: 1.0, Recency: 0.0, Importance: 0.0, SessionMatch: 0.0),
            halfLifeDays: 7.0);

        Assert.True(scoreNegative >= 0.0, $"Negative similarity should be clamped to 0, got {scoreNegative}");
    }

    /// <summary>
    /// UT-7: with similarity, recency, and session-match all held equal, a High-importance
    /// record (0.9, as mapped by MemorizeNow) must score strictly higher than a Low-importance
    /// record (0.3), and the gap must equal wᵢ · (0.9 - 0.3) under the default weights.
    /// </summary>
    [Fact]
    public void Score_HighImportance_0_9_RanksAboveLowImportance_0_3_AllElseEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var highFact = MakeRecord(importance: 0.9, updatedAt: now, sessionId: null);
        var lowFact = MakeRecord(importance: 0.3, updatedAt: now, sessionId: null);
        var weights = RankingWeights.Default;

        double scoreHigh = RankingFormula.Score(
            similarity: 0.8, record: highFact, currentSessionId: null, now: now, weights: weights, halfLifeDays: 7.0);
        double scoreLow = RankingFormula.Score(
            similarity: 0.8, record: lowFact, currentSessionId: null, now: now, weights: weights, halfLifeDays: 7.0);

        Assert.True(scoreHigh > scoreLow, $"Expected High-importance score ({scoreHigh}) > Low-importance score ({scoreLow})");
        Assert.True(Math.Abs((scoreHigh - scoreLow) - (weights.Importance * (0.9 - 0.3))) < 1e-9,
            $"Expected the score gap to equal wᵢ·(0.9-0.3)={weights.Importance * 0.6}, got {scoreHigh - scoreLow}");
    }

    /// <summary>
    /// UT-7: Normal importance (0.6) ranks strictly between High (0.9) and Low (0.3) when
    /// similarity, recency, and session-match are all held equal.
    /// </summary>
    [Fact]
    public void Score_NormalImportance_0_6_RanksBetweenHighAndLow()
    {
        var now = DateTimeOffset.UtcNow;
        var weights = RankingWeights.Default;

        double scoreHigh = RankingFormula.Score(0.8, MakeRecord(0.9, now), null, now, weights, 7.0);
        double scoreNormal = RankingFormula.Score(0.8, MakeRecord(0.6, now), null, now, weights, 7.0);
        double scoreLow = RankingFormula.Score(0.8, MakeRecord(0.3, now), null, now, weights, 7.0);

        Assert.True(scoreHigh > scoreNormal, $"Expected High ({scoreHigh}) > Normal ({scoreNormal})");
        Assert.True(scoreNormal > scoreLow, $"Expected Normal ({scoreNormal}) > Low ({scoreLow})");
    }
}
