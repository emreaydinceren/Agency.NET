using Agency.Acp.Dispatch;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Test.Dispatch;

/// <summary>
/// Behavioral tests for <see cref="IdentityPromptParser"/>: the two accepted
/// <c>_meta.systemPrompt</c> wire shapes, and the tolerated-unknown-shape rule from
/// Spec §6.1.
/// </summary>
public sealed class IdentityPromptParserTests
{
    /// <summary>An object carrying <c>{"append":"You are Ana"}</c> yields the appended text.</summary>
    [Fact]
    public void Parse_AppendObject_ReturnsText()
    {
        JObject token = new() { ["append"] = "You are Ana" };

        string? result = IdentityPromptParser.Parse(token);

        Assert.Equal("You are Ana", result);
    }

    /// <summary>A bare string is treated identically to <c>{"append": …}</c> (Spec §14.1).</summary>
    [Fact]
    public void Parse_BareString_ReturnsText()
    {
        JValue token = new("You are Ana");

        string? result = IdentityPromptParser.Parse(token);

        Assert.Equal("You are Ana", result);
    }

    /// <summary>A C# <c>null</c> <see cref="JToken"/> — <c>_meta.systemPrompt</c> absent entirely.</summary>
    [Fact]
    public void Parse_NullToken_ReturnsNull()
    {
        string? result = IdentityPromptParser.Parse(null);

        Assert.Null(result);
    }

    /// <summary>Inputs whose parsed meaning is "no identity" all normalise to <see langword="null"/>.</summary>
    [Theory]
    [MemberData(nameof(NullResultCases))]
    public void Parse_UnrecognisedOrEmptyShapes_ReturnsNull(JToken? token)
    {
        string? result = IdentityPromptParser.Parse(token);

        Assert.Null(result);
    }

    /// <summary>Inputs that should parse to <see langword="null"/>: absent, empty, whitespace-only, or an unrecognised shape.</summary>
    public static IEnumerable<object?[]> NullResultCases()
    {
        yield return new object?[] { JValue.CreateNull() };
        yield return new object?[] { new JObject() };
        yield return new object?[] { new JObject { ["replace"] = "X" } };
        yield return new object?[] { new JValue(string.Empty) };
        yield return new object?[] { new JValue("   ") };
        yield return new object?[] { new JValue(42) };
        yield return new object?[] { new JArray { "a" } };
        yield return new object?[] { new JObject { ["append"] = string.Empty } };
    }

    /// <summary>No exception escapes <see cref="IdentityPromptParser.Parse"/> for any input shape (Spec §8.1 step 2: "total").</summary>
    [Theory]
    [MemberData(nameof(AllCases))]
    public void Parse_AnyInput_NeverThrows(JToken? token)
    {
        Exception? exception = Record.Exception(() => IdentityPromptParser.Parse(token));

        Assert.Null(exception);
    }

    /// <summary>Every input shape from the parsing table, valid and invalid alike.</summary>
    public static IEnumerable<object?[]> AllCases()
    {
        yield return new object?[] { new JObject { ["append"] = "You are Ana" } };
        yield return new object?[] { new JValue("You are Ana") };
        yield return new object?[] { null };
        yield return new object?[] { JValue.CreateNull() };
        yield return new object?[] { new JObject() };
        yield return new object?[] { new JObject { ["replace"] = "X" } };
        yield return new object?[] { new JValue(string.Empty) };
        yield return new object?[] { new JValue("   ") };
        yield return new object?[] { new JValue(42) };
        yield return new object?[] { new JArray { "a" } };
        yield return new object?[] { new JObject { ["append"] = string.Empty } };
    }
}
