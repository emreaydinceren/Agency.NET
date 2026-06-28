namespace Agency.Configuration.Test;

/// <summary>
/// Unit tests for <see cref="PlaceholderExpander"/> covering the full expansion
/// algorithm: single tokens, embedded tokens, multiple tokens, chained references,
/// cycle detection, depth limiting, missing keys, escape sequences, and
/// case-insensitive lookup.
/// </summary>
public sealed class PlaceholderExpanderTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a fresh OrdinalIgnoreCase seed dictionary from the supplied pairs.
    /// </summary>
    private static Dictionary<string, string> Seed(params (string key, string value)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in pairs)
        {
            dict[key] = value;
        }

        return dict;
    }

    // -------------------------------------------------------------------------
    // Test 1 — single whole-value token
    // -------------------------------------------------------------------------

    /// <summary>
    /// A value that is entirely a single placeholder token is replaced by the
    /// looked-up string.
    /// </summary>
    [Fact]
    public void Expand_SingleWholeValueToken_ReturnsLookedUpValue()
    {
        var lookup = Seed(("Agent:BaseUrl", "http://llm.test:1234"));

        string result = PlaceholderExpander.Expand(
            "${Agent:BaseUrl}", "Embedding:BaseUrl", lookup);

        Assert.Equal("http://llm.test:1234", result);
    }

    // -------------------------------------------------------------------------
    // Test 2 — embedded token (suffix preserved)
    // -------------------------------------------------------------------------

    /// <summary>
    /// A placeholder embedded at the beginning of a value preserves the suffix
    /// that follows it.
    /// </summary>
    [Fact]
    public void Expand_EmbeddedToken_SuffixIsPreserved()
    {
        var lookup = Seed(("Agent:BaseUrl", "http://llm.test:1234"));

        string result = PlaceholderExpander.Expand(
            "${Agent:BaseUrl}/v1", "Embedding:BaseUrl", lookup);

        Assert.Equal("http://llm.test:1234/v1", result);
    }

    // -------------------------------------------------------------------------
    // Test 3 — multiple tokens in one value
    // -------------------------------------------------------------------------

    /// <summary>
    /// All placeholder tokens in a single value are replaced, left-to-right.
    /// </summary>
    [Fact]
    public void Expand_MultipleTokens_AllReplaced()
    {
        var lookup = Seed(("T:A", "hello"), ("T:B", "world"));

        string result = PlaceholderExpander.Expand(
            "${T:A}-${T:B}", "SomeKey", lookup);

        Assert.Equal("hello-world", result);
    }

    // -------------------------------------------------------------------------
    // Test 4 — chained references
    // -------------------------------------------------------------------------

    /// <summary>
    /// If the resolved value itself contains a placeholder, it is resolved
    /// recursively until a literal value is reached.
    /// </summary>
    [Fact]
    public void Expand_ChainedReferences_ResolvesToFinalLiteral()
    {
        // T:A -> ${T:B} -> "lit"
        var lookup = Seed(("T:A", "${T:B}"), ("T:B", "lit"));

        string result = PlaceholderExpander.Expand("${T:A}", "Root", lookup);

        Assert.Equal("lit", result);
    }

    // -------------------------------------------------------------------------
    // Test 5 — direct self-cycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// A key that references itself directly causes an <see cref="InvalidOperationException"/>
    /// whose message includes the key name to identify the cycle.
    /// </summary>
    [Fact]
    public void Expand_DirectCycle_ThrowsWithCycleInMessage()
    {
        var lookup = Seed(("T:A", "${T:A}"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PlaceholderExpander.Expand("${T:A}", "Root", lookup));

        Assert.Contains("T:A", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Test 6 — indirect cycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// An indirect cycle (A → B → A) causes an <see cref="InvalidOperationException"/>
    /// whose message shows the chain so the caller can diagnose the loop.
    /// </summary>
    [Fact]
    public void Expand_IndirectCycle_ThrowsWithChainInMessage()
    {
        var lookup = Seed(("T:A", "${T:B}"), ("T:B", "${T:A}"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PlaceholderExpander.Expand("${T:A}", "Root", lookup));

        // Message must show both keys so the chain is visible.
        Assert.Contains("T:A", ex.Message);
        Assert.Contains("T:B", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Test 7 — depth limit exceeded
    // -------------------------------------------------------------------------

    /// <summary>
    /// A chain longer than <c>MaxDepth</c> (32) causes an
    /// <see cref="InvalidOperationException"/> before stack overflow.
    /// </summary>
    [Fact]
    public void Expand_ChainExceedsMaxDepth_Throws()
    {
        // Build a 40-link chain: K:0 -> K:1 -> ... -> K:39 -> "leaf"
        const int chainLength = 40;
        var pairs = new (string, string)[chainLength + 1];
        for (int i = 0; i < chainLength; i++)
        {
            pairs[i] = ($"K:{i}", $"${{K:{i + 1}}}");
        }

        pairs[chainLength] = ($"K:{chainLength}", "leaf");
        var lookup = Seed(pairs);

        Assert.Throws<InvalidOperationException>(
            () => PlaceholderExpander.Expand("${K:0}", "Root", lookup));
    }

    // -------------------------------------------------------------------------
    // Test 8 — missing key
    // -------------------------------------------------------------------------

    /// <summary>
    /// A configuration placeholder (containing ':') whose key is absent from the
    /// lookup throws an <see cref="InvalidOperationException"/> naming both the
    /// missing token and the owning key.
    /// </summary>
    [Fact]
    public void Expand_MissingKey_ThrowsNamingTokenAndOwningKey()
    {
        var lookup = Seed(("Existing:Key", "value"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => PlaceholderExpander.Expand("${Nope:Missing}", "Some:Key", lookup));

        Assert.Contains("${Nope:Missing}", ex.Message);
        Assert.Contains("Some:Key", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Test 9 — escape sequence
    // -------------------------------------------------------------------------

    /// <summary>
    /// The escape sequence <c>$$</c> yields a literal <c>$</c>, so
    /// <c>$${X}</c> produces the literal text <c>${X}</c> without looking up X.
    /// </summary>
    [Fact]
    public void Expand_EscapeSequence_ProducesLiteralDollarWithoutLookup()
    {
        // Put a distinct value under X so we can prove it was never retrieved.
        var lookup = Seed(("X", "SHOULD_NOT_APPEAR"));

        string result = PlaceholderExpander.Expand("$${X}", "SomeKey", lookup);

        Assert.Equal("${X}", result);
        Assert.DoesNotContain("SHOULD_NOT_APPEAR", result);
    }

    // -------------------------------------------------------------------------
    // Test 10 — case-insensitive lookup
    // -------------------------------------------------------------------------

    /// <summary>
    /// Placeholder keys are matched case-insensitively against the lookup
    /// dictionary, mirroring <see cref="IConfiguration"/> semantics.
    /// </summary>
    [Fact]
    public void Expand_CaseInsensitiveKey_Resolves()
    {
        var lookup = Seed(("Agent:BaseUrl", "http://llm.test:1234"));

        // Token uses all-lowercase; seed key is PascalCase-with-colon.
        string result = PlaceholderExpander.Expand(
            "${agent:baseurl}", "SomeKey", lookup);

        Assert.Equal("http://llm.test:1234", result);
    }

    // -------------------------------------------------------------------------
    // Test 11 — value with no tokens
    // -------------------------------------------------------------------------

    /// <summary>
    /// A value that contains no placeholder tokens is returned unchanged.
    /// </summary>
    [Fact]
    public void Expand_NoTokens_ReturnedUnchanged()
    {
        var lookup = Seed(("A", "irrelevant"));
        const string plainValue = "just-a-plain-string";

        string result = PlaceholderExpander.Expand(plainValue, "SomeKey", lookup);

        Assert.Equal(plainValue, result);
    }

    // -------------------------------------------------------------------------
    // Test 12 — bare (colon-less) tokens belong to other resolvers
    // -------------------------------------------------------------------------

    /// <summary>
    /// A token with no ':' path separator (e.g. the MCP <c>${RepoRoot}</c> /
    /// <c>${Configuration}</c> tokens) is left verbatim and is neither resolved nor
    /// treated as a missing key — even when an embedded ${Section:Key} alongside it
    /// is resolved, and even when a same-named bare key happens to exist in the seed.
    /// </summary>
    [Fact]
    public void Expand_BareToken_LeftVerbatim_AndDoesNotThrow()
    {
        var lookup = Seed(
            ("Configuration", "SHOULD_NOT_BE_USED"),
            ("Build:Output", "bin/Debug"));

        string result = PlaceholderExpander.Expand(
            "${RepoRoot}/src/${Build:Output}/${Configuration}/x.dll", "Mcp:Args", lookup);

        // Bare tokens pass through untouched; only the colon-bearing token is expanded.
        Assert.Equal("${RepoRoot}/src/bin/Debug/${Configuration}/x.dll", result);
        Assert.DoesNotContain("SHOULD_NOT_BE_USED", result);
    }
}
