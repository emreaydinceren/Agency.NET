using Agency.Llm.Common;

namespace Agency.Llm.Test;

/// <summary>
/// Unit tests for <see cref="Model"/>: verifies the two-argument constructor stays
/// source-compatible and that optional metadata defaults to <see langword="null"/>
/// (unknown, per P3), never a claim.
/// </summary>
public sealed class ModelTests
{
    /// <summary>The two-argument constructor shape must stay source-compatible.</summary>
    [Fact]
    public void Constructor_TwoArgs_StillCompiles()
    {
        var model = new Model("id", "name");

        Assert.Equal("id", model.Id);
        Assert.Equal("name", model.Name);
    }

    /// <summary>Optional metadata defaults to null (unknown), never a false claim (P3).</summary>
    [Fact]
    public void OptionalMetadata_DefaultsToNull()
    {
        var model = new Model("id", "name");

        Assert.Null(model.Kind);
        Assert.Null(model.ContextLength);
        Assert.Null(model.IsLoaded);
    }

    /// <summary><see cref="ModelKind"/> defines at least Chat, Embedding, and Unknown.</summary>
    [Theory]
    [InlineData(ModelKind.Chat)]
    [InlineData(ModelKind.Embedding)]
    [InlineData(ModelKind.Unknown)]
    public void ModelKind_HasExpectedValues(ModelKind kind)
    {
        Assert.True(Enum.IsDefined(kind));
    }
}
