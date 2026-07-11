using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agency.Harness.Skills;

/// <summary>
/// Pure string transform that substitutes placeholders in a skill body.
/// No I/O or shell execution — shell injection is a Phase-2 concern (Task 7).
/// </summary>
internal static class SkillRenderer
{
    // Index base choice: 1-based (shell convention).
    // $1 / $ARGUMENTS[1] → first whitespace-separated token of arguments.
    // $0 / $ARGUMENTS[0] → the full arguments string (mirrors $ARGUMENTS).
    // Named $name placeholders use 1-based position from skill.Arguments list.

    /// <summary>
    /// Renders <paramref name="skill"/>.Body with all placeholder substitutions applied.
    /// </summary>
    /// <param name="skill">The skill whose body is rendered.</param>
    /// <param name="arguments">The raw argument string passed by the invoker, or <see langword="null"/>.</param>
    /// <param name="sessionId">The current session identifier.</param>
    /// <returns>The rendered body string.</returns>
    internal static string Render(Skill skill, string? arguments, string sessionId)
    {
        string args = arguments ?? string.Empty;
        IReadOnlyList<string> tokens = TokenizeArguments(args);

        bool bodyHasArgumentsPlaceholder = ContainsArgumentsPlaceholder(skill.Body);

        string rendered = SubstitutePlaceholders(skill.Body, skill, args, tokens, sessionId);

        // Fallback: when no $ARGUMENTS placeholder existed but arguments were provided,
        // append "ARGUMENTS: <value>" on a new line.
        if (!bodyHasArgumentsPlaceholder && args.Length > 0)
        {
            rendered = rendered.TrimEnd('\r', '\n') + "\r\nARGUMENTS: " + args;
        }

        return rendered;
    }

    // ---------------------------------------------------------------------------
    // Shell expansion — layered on top of the pure Render; runner-injected
    // ---------------------------------------------------------------------------

    // Matches fenced ```! blocks: opening fence, any content, closing ```.
    // Uses non-greedy so the first closing ``` ends the match.
    private static readonly Regex FencedShellPattern = new(
        @"```!\r?\n([\s\S]*?)```",
        RegexOptions.Compiled);

    // Matches inline backtick shell directives:  !`command`
    private static readonly Regex InlineShellPattern = new(
        @"!`([^`]+)`",
        RegexOptions.Compiled);

    /// <summary>
    /// Performs shell expansion on an already-rendered skill body.
    /// <para>
    /// Expansion is intentionally a SEPARATE step from <see cref="Render"/> so the pure
    /// string-transform logic remains trivially unit-testable without any I/O.
    /// </para>
    /// <para>
    /// Security note: expansion is single-pass — the output of a shell command is never
    /// re-scanned for further <c>!</c> directives, preventing recursion / injection escalation.
    /// When <paramref name="disabled"/> is <see langword="true"/> or <paramref name="runner"/>
    /// is <see langword="null"/> the directives are left verbatim in the output (visible but
    /// inert) so the model can see that shell expansion was requested but not executed.
    /// </para>
    /// </summary>
    /// <param name="renderedBody">The output of <see cref="Render"/> — already substitution-expanded.</param>
    /// <param name="runner">The shell runner to use, or <see langword="null"/> to skip expansion.</param>
    /// <param name="disabled">When <see langword="true"/>, no runner is invoked (directives left intact).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The body with all <c>!</c>-directives replaced by their shell output.</returns>
    internal static async Task<string> ExpandShellAsync(
        string renderedBody,
        ISkillShellRunner? runner,
        bool disabled,
        CancellationToken ct = default)
    {
        if (disabled || runner is null)
        {
            return renderedBody;
        }

        // Step 1 — expand fenced ```! blocks first (they are structurally larger and must be
        //           matched before inline !`…` could accidentally pick up content inside them).
        string afterFenced = await ExpandFencedBlocksAsync(renderedBody, runner, ct).ConfigureAwait(false);

        // Step 2 — expand inline !`cmd` directives over the result of step 1.
        //           Because we expand in two ordered steps over the same string, and never
        //           re-scan the *output* of any expansion, this is effectively one pass.
        string afterInline = await ExpandInlineAsync(afterFenced, runner, ct).ConfigureAwait(false);

        return afterInline;
    }

    /// <summary>Replaces all fenced <c>```!</c> … <c>```</c> blocks with their shell output.</summary>
    private static async Task<string> ExpandFencedBlocksAsync(
        string text, ISkillShellRunner runner, CancellationToken ct)
    {
        MatchCollection matches = FencedShellPattern.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        // Pre-run all commands; do not interleave with string building to keep ordering predictable.
        string[] outputs = new string[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            string command = matches[i].Groups[1].Value.Trim();
            outputs[i] = await runner.RunAsync(command, ct).ConfigureAwait(false);
        }

        // Rebuild the string, replacing each match with the corresponding output.
        // Walking backwards preserves indices for earlier matches.
        StringBuilder sb = new(text);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            sb.Remove(matches[i].Index, matches[i].Length);
            sb.Insert(matches[i].Index, outputs[i]);
        }

        return sb.ToString();
    }

    /// <summary>Replaces all inline <c>!`cmd`</c> directives with their shell output.</summary>
    private static async Task<string> ExpandInlineAsync(
        string text, ISkillShellRunner runner, CancellationToken ct)
    {
        MatchCollection matches = InlineShellPattern.Matches(text);
        if (matches.Count == 0)
        {
            return text;
        }

        string[] outputs = new string[matches.Count];
        for (int i = 0; i < matches.Count; i++)
        {
            string command = matches[i].Groups[1].Value;
            outputs[i] = await runner.RunAsync(command, ct).ConfigureAwait(false);
        }

        StringBuilder sb = new(text);
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            sb.Remove(matches[i].Index, matches[i].Length);
            sb.Insert(matches[i].Index, outputs[i]);
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------------------
    // Tokenizer — shell-style: respects double and single quotes
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Splits <paramref name="input"/> into whitespace-delimited tokens, respecting
    /// double-quoted and single-quoted spans so <c>"foo bar" baz</c> yields
    /// <c>["foo bar", "baz"]</c>.
    /// </summary>
    private static List<string> TokenizeArguments(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        List<string> tokens = [];
        StringBuilder current = new();
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                i++;
                continue;
            }

            if (c == '"')
            {
                // Consume everything up to the closing double-quote.
                i++;
                while (i < input.Length && input[i] != '"')
                {
                    current.Append(input[i]);
                    i++;
                }
                if (i < input.Length)
                {
                    i++; // skip closing "
                }
                continue;
            }

            if (c == '\'')
            {
                // Consume everything up to the closing single-quote.
                i++;
                while (i < input.Length && input[i] != '\'')
                {
                    current.Append(input[i]);
                    i++;
                }
                if (i < input.Length)
                {
                    i++; // skip closing '
                }
                continue;
            }

            current.Append(c);
            i++;
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    // ---------------------------------------------------------------------------
    // Substitution — single-pass regex replacement
    // ---------------------------------------------------------------------------

    // Matches:
    //   \$          → escaped dollar sign (render as literal $)
    //   ${CLAUDE_SKILL_DIR}
    //   ${CLAUDE_SESSION_ID}
    //   $ARGUMENTS[N]  (N = integer)
    //   $ARGUMENTS
    //   $N             (N = integer, 1-based positional)
    //   $name          (named argument from skill.Arguments)
    // Order matters: longer patterns must appear before shorter ones.
    private static readonly Regex SubstitutionPattern = new(
        @"\\\$" +                                       // \$ — escaped dollar, must come first
        @"|(?<!\\\$)\$\{CLAUDE_SKILL_DIR\}" +
        @"|(?<!\\\$)\$\{CLAUDE_SESSION_ID\}" +
        @"|(?<!\\\$)\$ARGUMENTS\[(\d+)\]" +
        @"|(?<!\\\$)\$ARGUMENTS" +
        @"|(?<!\\\$)\$(\d+)" +
        @"|(?<!\\\$)\$([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    // Matches any unescaped argument-consuming placeholder: $ARGUMENTS, $ARGUMENTS[N], $N, $name.
    // Used to decide whether the "ARGUMENTS: <value>" fallback append is needed.
    // The fallback only applies when the body has no placeholder that already consumes arguments.
    private static readonly Regex ArgumentsConsumingPattern = new(
        @"(?<!\\)\$ARGUMENTS(?:\[\d+\])?" +   // $ARGUMENTS or $ARGUMENTS[N]
        @"|(?<!\\)\$\d+" +                     // $N (numeric)
        @"|(?<!\\)\$[A-Za-z_][A-Za-z0-9_]*",  // $name (identifier)
        RegexOptions.Compiled);

    private static bool ContainsArgumentsPlaceholder(string body) =>
        ArgumentsConsumingPattern.IsMatch(body);

    private static string SubstitutePlaceholders(
        string body,
        Skill skill,
        string args,
        IReadOnlyList<string> tokens,
        string sessionId)
    {
        return SubstitutionPattern.Replace(body, match =>
        {
            string value = match.Value;

            // \$ → literal $
            if (value == @"\$")
            {
                return "$";
            }

            // ${CLAUDE_SKILL_DIR}
            if (value == "${CLAUDE_SKILL_DIR}")
            {
                return skill.SkillDir;
            }

            // ${CLAUDE_SESSION_ID}
            if (value == "${CLAUDE_SESSION_ID}")
            {
                return sessionId;
            }

            // $ARGUMENTS[N] — capture group 1
            if (value.StartsWith("$ARGUMENTS[", StringComparison.Ordinal))
            {
                int n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return ResolvePositional(n, args, tokens);
            }

            // $ARGUMENTS — full argument string
            if (value == "$ARGUMENTS")
            {
                return args;
            }

            // $N — numeric positional (1-based), capture group 2
            if (match.Groups[2].Success)
            {
                int n = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                return ResolvePositional(n, args, tokens);
            }

            // $name — named argument, capture group 3
            if (match.Groups[3].Success)
            {
                string name = match.Groups[3].Value;
                int idx = IndexOfArgumentName(skill.Arguments, name);
                if (idx >= 0)
                {
                    // Named args are 1-based: idx 0 in the list → token 1.
                    return ResolvePositional(idx + 1, args, tokens);
                }

                // Unknown name — leave the placeholder as-is.
                return value;
            }

            // Fallthrough — leave unchanged.
            return value;
        });
    }

    /// <summary>
    /// Resolves a 1-based positional index.
    /// Index 0 (or $ARGUMENTS[0]) returns the full argument string.
    /// Index N returns the Nth token (1 = first token).
    /// Out-of-range returns an empty string.
    /// </summary>
    private static string ResolvePositional(int n, string args, IReadOnlyList<string> tokens)
    {
        if (n == 0)
        {
            return args;
        }

        int tokenIndex = n - 1; // convert 1-based to 0-based
        return tokenIndex < tokens.Count ? tokens[tokenIndex] : string.Empty;
    }

    private static int IndexOfArgumentName(IReadOnlyList<string> argumentNames, string name)
    {
        for (int i = 0; i < argumentNames.Count; i++)
        {
            if (string.Equals(argumentNames[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
