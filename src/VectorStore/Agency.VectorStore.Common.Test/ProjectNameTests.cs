namespace Agency.VectorStore.Common.Test;

/// <summary>
/// Unit tests for <see cref="ProjectName"/> validation, canonicalisation, and idempotence
/// (Spec §6.1, §8.1).
/// </summary>
public sealed class ProjectNameTests
{
    // -------------------------------------------------------------------------
    // TryNormalize - Valid names
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that valid names are trimmed and lowercased to their canonical form.
    /// </summary>
    [Theory]
    [InlineData("handbook", "handbook")]
    [InlineData("Handbook", "handbook")]
    [InlineData(" Q3-Report ", "q3-report")]
    [InlineData("v1.2_notes", "v1.2_notes")]
    public void TryNormalize_ValidNames_ReturnsCanonicalLowercase(string raw, string expectedCanonical)
    {
        var result = ProjectName.TryNormalize(raw, out var canonical, out var error);

        Assert.True(result);
        Assert.Equal(expectedCanonical, canonical);
        Assert.Null(error);
    }

    // -------------------------------------------------------------------------
    // TryNormalize - Invalid names
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that invalid names are rejected with a non-empty reason.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("a b")]
    [InlineData("docs/2026")]
    [InlineData("-leading")]
    [InlineData("has]bracket")]
    [InlineData("line\nbreak")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 characters
    public void TryNormalize_InvalidNames_ReturnsFalseWithReason(string raw)
    {
        var result = ProjectName.TryNormalize(raw, out _, out var error);

        Assert.False(result);
        Assert.False(string.IsNullOrEmpty(error));
    }

    // -------------------------------------------------------------------------
    // EnsureValid - Invalid names
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that EnsureValid throws ArgumentException with paramName set for an invalid name.
    /// </summary>
    [Fact]
    public void EnsureValid_InvalidName_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProjectName.EnsureValid("*"));

        Assert.Equal("raw", exception.ParamName);
    }

    // -------------------------------------------------------------------------
    // TryNormalize - Idempotence
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that normalizing an already-canonical value again produces the same result.
    /// </summary>
    [Fact]
    public void TryNormalize_IsIdempotent()
    {
        ProjectName.TryNormalize(" Q3-Report ", out var firstCanonical, out _);

        var result = ProjectName.TryNormalize(firstCanonical, out var secondCanonical, out var secondError);

        Assert.True(result);
        Assert.Equal(firstCanonical, secondCanonical);
        Assert.Null(secondError);
    }
}
