using Newtonsoft.Json.Linq;

namespace Agency.Acp.Dispatch;

/// <summary>
/// Turns the untyped <c>_meta.systemPrompt</c> token from <c>session/new</c> into a
/// Persona identity string, tolerating both wire shapes ACP clients send.
/// </summary>
/// <remarks>
/// Both the <c>{"append": "…"}</c> object form and a bare string carry the same, append-only
/// semantics: the opening identity line of the system prompt is replaced, while the harness's
/// ReAct instruction, skills catalogue and grounding survive verbatim. A bare string is
/// therefore not a second mode — it is shorthand for the first.
/// See Spec §14.1 for why a bare string is not treated as a full replace.
/// </remarks>
internal static class IdentityPromptParser
{
    /// <summary>
    /// Parses <paramref name="token"/> into an identity string, or <see langword="null"/> when
    /// the token is absent, empty, whitespace-only, or an unrecognised shape.
    /// </summary>
    /// <param name="token">The <c>_meta.systemPrompt</c> value from <c>session/new</c>, or <see langword="null"/> when absent.</param>
    /// <returns>The identity text, or <see langword="null"/> meaning "use the default identity line".</returns>
    /// <remarks>
    /// This is a total function: every branch is a type test, never a cast or <c>Value&lt;string&gt;()</c>
    /// call that could throw, and an unrecognised shape degrades to <see langword="null"/> rather
    /// than erroring, so a forward-compatible client cannot break session creation.
    /// </remarks>
    internal static string? Parse(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }

        if (token is JValue { Type: JTokenType.String } stringValue)
        {
            return Normalise(stringValue.Value as string);
        }

        if (token is JObject obj && obj["append"] is JValue { Type: JTokenType.String } appendValue)
        {
            return Normalise(appendValue.Value as string);
        }

        return null;
    }

    private static string? Normalise(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
