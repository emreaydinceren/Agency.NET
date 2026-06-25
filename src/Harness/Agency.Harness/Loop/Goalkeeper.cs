namespace Agency.Harness.Loop;

/// <summary>
/// Runs a deterministic, transcript-only done-check on a cheap, independent
/// <see cref="IChatClient"/> (resolved externally — see <c>Models.CreateChatClient</c>).
/// Implements the Judge gate described in §6.3 of the Loop Kit spec.
/// </summary>
/// <remarks>
/// Response format expected from the cheap model (case-insensitive on the keyword):
/// <code>
/// VERDICT: done
/// REASON: &lt;short sentence&gt;
/// </code>
/// or
/// <code>
/// VERDICT: continue
/// REASON: &lt;short sentence&gt;
/// </code>
/// Anything else is treated as <c>Continue("verdict unparseable")</c> per E-2.
/// </remarks>
internal sealed class Goalkeeper : IGoalkeeper
{
    private readonly IChatClient _client;
    private readonly string _model;
    private readonly string _clientType;
    private readonly string? _rubric;

    /// <summary>
    /// Initialises the Goalkeeper with its own dedicated cheap client.
    /// </summary>
    /// <param name="client">
    /// The cheap <see cref="IChatClient"/> used exclusively for verdict calls.
    /// Must be independent of the worker's client (self-preference mitigation, T-GK-4).
    /// </param>
    /// <param name="model">
    /// The model id to request — passed via <see cref="ChatOptions.ModelId"/> (gotcha 7:
    /// <c>Models.CreateChatClient</c> returns the client but not the model id).
    /// </param>
    /// <param name="clientType">
    /// Provider display name used in telemetry tags (e.g. <c>"Claude"</c>).
    /// Mirrors the <see cref="Agent"/> pattern where <c>clientType</c> is supplied
    /// externally by <c>Models.CreateChatClient</c>.
    /// </param>
    /// <param name="rubric">
    /// Optional extra strictness instructions appended to the system prompt
    /// (maps to <c>LoopOptions.GoalkeeperRubric</c>).
    /// </param>
    internal Goalkeeper(
        IChatClient client,
        string model,
        string? clientType = null,
        string? rubric = null)
    {
        this._client = client ?? throw new ArgumentNullException(nameof(client));
        this._model = string.IsNullOrWhiteSpace(model)
            ? throw new ArgumentException("Model id must not be blank.", nameof(model))
            : model;
        this._clientType = clientType ?? "Unknown";
        this._rubric = rubric;
    }

    /// <inheritdoc/>
    public async Task<Verdict> EvaluateAsync(
        string condition,
        IReadOnlyList<ChatMessage> transcript,
        CancellationToken ct)
    {
        // Build options — no tools (NG-4), explicit model id (gotcha 7).
        var options = new ChatOptions
        {
            ModelId = this._model,
            Instructions = GoalkeeperPromptBuilder.BuildSystemPrompt(this._rubric),
            // Tools intentionally omitted — the Goalkeeper is transcript-only.
        };

        ChatMessage userMessage = GoalkeeperPromptBuilder.BuildUserMessage(condition, transcript);

        ChatResponse response = await this._client.GetResponseAsync(
            [userMessage],
            options,
            ct);

        // Emit goalkeeper token counters (§10: role=goalkeeper).
        if (response.Usage is { } usage)
        {
            LoopRunner.EmitGoalkeeperTokens(
                usage.InputTokenCount ?? 0,
                usage.OutputTokenCount ?? 0,
                this._model,
                this._clientType);
        }

        // ChatResponse.Messages is a list; take the last assistant message's text.
        ChatMessage? lastMsg = response.Messages.LastOrDefault(static m => m.Role == ChatRole.Assistant);
        string rawText = lastMsg?.Text ?? string.Empty;
        return ParseVerdict(rawText);
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the cheap model's raw text response into a <see cref="Verdict"/>.
    /// Returns <c>Continue("verdict unparseable")</c> when the response does not
    /// match the expected format (E-2).
    /// </summary>
    private static Verdict ParseVerdict(string raw)
    {
        // Expected format (case-insensitive):
        //   VERDICT: done   (or continue)
        //   REASON: <text>
        string? verdictKeyword = null;
        string? reason = null;

        foreach (string line in raw.Split('\n'))
        {
            string trimmedLine = line.Trim();

            if (verdictKeyword is null && StartsWithIgnoreCase(trimmedLine, "VERDICT:"))
            {
                verdictKeyword = trimmedLine["VERDICT:".Length..].Trim();
            }
            else if (reason is null && StartsWithIgnoreCase(trimmedLine, "REASON:"))
            {
                reason = trimmedLine["REASON:".Length..].Trim();
            }
        }

        if (verdictKeyword is null || reason is null)
        {
            return new Verdict.Continue("verdict unparseable");
        }

        return verdictKeyword.Equals("done", StringComparison.OrdinalIgnoreCase)
            ? new Verdict.Done(reason)
            : verdictKeyword.Equals("continue", StringComparison.OrdinalIgnoreCase)
                ? new Verdict.Continue(reason)
                : new Verdict.Continue("verdict unparseable");
    }

    private static bool StartsWithIgnoreCase(string line, string prefix) =>
        line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
