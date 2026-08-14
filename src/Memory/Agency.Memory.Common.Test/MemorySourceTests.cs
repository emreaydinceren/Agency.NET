using System.Text.Json;
using Agency.Memory.Common.Records;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Common.Test;

/// <summary>Tests for the <see cref="MemorySource"/> enum and its integration with <see cref="MemoryRecord"/>.</summary>
public sealed class MemorySourceTests
{
    /// <summary>Verifies that the underlying integer values match the DB SMALLINT column mapping.</summary>
    [Fact]
    public void EnumValues_AreCorrect()
    {
        Assert.Equal(0, (int)MemorySource.AgentSignaled);
        Assert.Equal(1, (int)MemorySource.Distilled);
        Assert.Equal(2, (int)MemorySource.Consolidated);
        Assert.Equal(3, (int)MemorySource.HygieneManual);
    }

    /// <summary>
    /// Verifies that <see cref="MemorySource"/> has no <c>JsonStringEnumConverter</c> applied, so
    /// <see cref="JsonSerializer"/> serializes it as its underlying numeric value (the codebase-wide
    /// default for enums; unlike the design doc's illustrative snippet, no string converter is registered).
    /// </summary>
    [Theory]
    [InlineData(MemorySource.AgentSignaled, "0")]
    [InlineData(MemorySource.Distilled, "1")]
    [InlineData(MemorySource.Consolidated, "2")]
    [InlineData(MemorySource.HygieneManual, "3")]
    public void Enum_SerializesToJson_AsUnderlyingInteger(MemorySource source, string expectedJson)
    {
        string json = JsonSerializer.Serialize(source);
        Assert.Equal(expectedJson, json);
    }

    /// <summary>Verifies that a numeric JSON value deserializes back to the matching enum member.</summary>
    [Theory]
    [InlineData("0", MemorySource.AgentSignaled)]
    [InlineData("1", MemorySource.Distilled)]
    [InlineData("2", MemorySource.Consolidated)]
    [InlineData("3", MemorySource.HygieneManual)]
    public void Enum_DeserializesFromJson_AsInteger(string json, MemorySource expected)
    {
        MemorySource result = JsonSerializer.Deserialize<MemorySource>(json);
        Assert.Equal(expected, result);
    }

    /// <summary>Verifies that every <see cref="MemorySource"/> value round-trips through JSON without data loss.</summary>
    [Theory]
    [InlineData(MemorySource.AgentSignaled)]
    [InlineData(MemorySource.Distilled)]
    [InlineData(MemorySource.Consolidated)]
    [InlineData(MemorySource.HygieneManual)]
    public void Enum_RoundTripsThroughJson_WithoutDataLoss(MemorySource source)
    {
        string json = JsonSerializer.Serialize(source);
        MemorySource roundTripped = JsonSerializer.Deserialize<MemorySource>(json);
        Assert.Equal(source, roundTripped);
    }

    /// <summary>Verifies that a <see cref="MemoryRecord"/> constructed with an explicit source stores that value.</summary>
    [Fact]
    public void Record_WithSource_StoresValue()
    {
        var now = DateTimeOffset.UtcNow;

        var record = MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Test",
            key: "test-key",
            title: "Test",
            value: "Test value",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now,
            source: MemorySource.AgentSignaled);

        Assert.Equal(MemorySource.AgentSignaled, record.Source);
    }

    /// <summary>Verifies that omitting <c>source</c> in <see cref="MemoryRecord.Create"/> defaults to <see cref="MemorySource.Distilled"/> for backward compatibility with pre-existing Distiller call sites.</summary>
    [Fact]
    public void Record_DefaultSource_IsDistilled()
    {
        var now = DateTimeOffset.UtcNow;

        var record = MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Test",
            key: "test-key",
            title: "Test",
            value: "Test value",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now);

        Assert.Equal(MemorySource.Distilled, record.Source);
    }

    /// <summary>Verifies that each <see cref="MemorySource"/> value correctly identifies its provenance when round-tripped through a <see cref="MemoryRecord"/>.</summary>
    [Theory]
    [InlineData(MemorySource.AgentSignaled)]
    [InlineData(MemorySource.Distilled)]
    [InlineData(MemorySource.Consolidated)]
    [InlineData(MemorySource.HygieneManual)]
    public void Record_Source_IdentifiesProvenance(MemorySource source)
    {
        var now = DateTimeOffset.UtcNow;

        var record = MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Test",
            key: "test-key",
            title: "Test",
            value: "Test value",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now,
            source: source);

        Assert.Equal(source, record.Source);
    }

    /// <summary>Verifies that the <c>Source</c> property participates in non-destructive <c>with</c>-expression copies.</summary>
    [Fact]
    public void Record_WithExpression_CanChangeSource()
    {
        var now = DateTimeOffset.UtcNow;
        var original = MemoryRecord.Create(
            id: "r1",
            userId: "u1",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Test",
            key: "test-key",
            title: "Test",
            value: "Test value",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now,
            source: MemorySource.Distilled);

        var consolidated = original with { Source = MemorySource.Consolidated };

        Assert.Equal(MemorySource.Distilled, original.Source);
        Assert.Equal(MemorySource.Consolidated, consolidated.Source);
    }
}
