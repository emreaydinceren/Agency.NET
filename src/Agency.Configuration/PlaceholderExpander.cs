using System.Text.RegularExpressions;

namespace Agency.Configuration;

/// <summary>
/// Pure, stateless helper that expands <c>${Section:Key}</c> placeholder tokens
/// inside configuration values, mirroring the substitution style of
/// <c>McpConfigResolver</c> but adding recursive chaining, cycle detection,
/// and a depth guard.
/// </summary>
/// <remarks>
/// <para>
/// Tokens are matched left-to-right by the regex
/// <c>(?&lt;escape&gt;\$\$)|\$\{(?&lt;key&gt;[^}]+)\}</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>$$</c> → literal <c>$</c> (so <c>$${X}</c> yields the literal text
///     <c>${X}</c> without any lookup).
///   </description></item>
///   <item><description>
///     <c>${Section:Key}</c> (a token containing the <c>:</c> path separator) →
///     looked up case-insensitively in the caller-supplied <c>lookup</c> dictionary.
///     If the resolved value itself contains tokens, they are expanded recursively.
///   </description></item>
///   <item><description>
///     <c>${Bare}</c> (a token with no <c>:</c>, e.g. <c>${RepoRoot}</c>) → left
///     verbatim. Such tokens belong to other substitution systems (e.g.
///     <c>McpConfigResolver</c>) and are deliberately neither resolved nor failed on
///     here, so the two systems can share the <c>${…}</c> syntax without colliding.
///   </description></item>
/// </list>
/// <para>
/// A <see cref="List{T}"/> of keys on the current resolution path is maintained so
/// that direct and indirect cycles are caught and reported with a readable chain
/// (e.g. <c>A -&gt; B -&gt; A</c>) before a stack overflow can occur.
/// Additionally, <see cref="MaxDepth"/> limits the recursion depth as a backstop.
/// </para>
/// </remarks>
internal static partial class PlaceholderExpander
{
    /// <summary>Maximum recursion depth for chained placeholder resolution.</summary>
    private const int MaxDepth = 32;

    [GeneratedRegex(@"(?<escape>\$\$)|\$\{(?<key>[^}]+)\}", RegexOptions.ExplicitCapture)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Expands all <c>${key}</c> placeholder tokens found in <paramref name="value"/>
    /// by looking each key up in <paramref name="lookup"/>.
    /// </summary>
    /// <param name="value">
    /// The raw configuration value that may contain placeholder tokens.
    /// Returned unchanged when it contains no tokens.
    /// </param>
    /// <param name="owningKey">
    /// The configuration key whose value is being expanded. Used only in
    /// diagnostic messages so callers can trace the source of an error.
    /// </param>
    /// <param name="lookup">
    /// The full set of merged configuration key/value pairs to resolve against.
    /// The caller should supply an <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// dictionary; this method performs case-insensitive lookups regardless.
    /// </param>
    /// <returns>
    /// The value with every placeholder token replaced by its resolved string.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a referenced key is absent from <paramref name="lookup"/>,
    /// when the resolution chain contains a cycle, or when the chain length
    /// exceeds <see cref="MaxDepth"/>.
    /// </exception>
    internal static string Expand(
        string value,
        string owningKey,
        IReadOnlyDictionary<string, string> lookup)
    {
        return ExpandCore(value, owningKey, lookup, [], 0);
    }

    private static string ExpandCore(
        string value,
        string owningKey,
        IReadOnlyDictionary<string, string> lookup,
        List<string> resolutionPath,
        int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"Configuration placeholder resolution exceeded the maximum depth of {MaxDepth} " +
                $"while resolving key \"{owningKey}\".");
        }

        return TokenPattern().Replace(value, match =>
        {
            // $$ → literal $
            if (match.Groups["escape"].Success)
            {
                return "$";
            }

            string key = match.Groups["key"].Value;

            // Only ${Section:Key} tokens — those containing the ':' configuration path
            // separator — are treated as configuration references. Bare tokens such as
            // ${RepoRoot} or ${Configuration} belong to other token systems (e.g.
            // McpConfigResolver, which expands them later against runtime paths) and are
            // left verbatim so this resolver neither claims nor fails on them.
            if (!key.Contains(':'))
            {
                return match.Value;
            }

            string? resolved = FindValue(lookup, key);
            if (resolved is null)
            {
                throw new InvalidOperationException(
                    $"Configuration placeholder \"${{{key}}}\" referenced by key " +
                    $"\"{owningKey}\" could not be resolved.");
            }

            // Cycle detection: if this key is already on the resolution path,
            // report the chain and refuse to recurse.
            int existingIndex = resolutionPath.FindIndex(
                k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                string chain = string.Join(" -> ", resolutionPath) + " -> " + key;
                throw new InvalidOperationException(
                    $"Configuration placeholder cycle detected: {chain}");
            }

            resolutionPath.Add(key);
            try
            {
                return ExpandCore(resolved, owningKey, lookup, resolutionPath, depth + 1);
            }
            finally
            {
                resolutionPath.RemoveAt(resolutionPath.Count - 1);
            }
        });
    }

    /// <summary>
    /// Looks up <paramref name="key"/> case-insensitively in <paramref name="source"/>,
    /// returning the value when found or <see langword="null"/> when absent.
    /// Works correctly regardless of the dictionary's own comparer.
    /// </summary>
    private static string? FindValue(IReadOnlyDictionary<string, string> source, string key)
    {
        // Fast path: works when the dictionary is already OrdinalIgnoreCase.
        if (source.TryGetValue(key, out string? value))
        {
            return value;
        }

        // Slow path: linear scan for callers that supplied a case-sensitive dictionary.
        foreach (KeyValuePair<string, string> kvp in source)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }
}
