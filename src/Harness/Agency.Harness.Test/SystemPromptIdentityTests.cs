using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for <see cref="QueryContext.IdentityPrompt"/> (spec §6.9 D-3, §4 P1, §9): a Persona may
/// replace the opening identity line of the system prompt without disturbing anything else the
/// builder assembles, and the replacement holds on every loop iteration because the prompt is
/// rebuilt from <see cref="Context"/> each time.
/// </summary>
public sealed class SystemPromptIdentityTests
{
    private const string DefaultIdentityLine = "You are an autonomous agent operating inside the Agency runtime.";

    private static Context MakeContext(string? identityPrompt) =>
        new()
        {
            Query = new QueryContext { Prompt = "Hello", IdentityPrompt = identityPrompt },
        };

    // ── (a), (b): the identity line is replaced, nothing else is ───────────────

    /// <summary>With no <see cref="QueryContext.IdentityPrompt"/>, the default identity line is emitted verbatim.</summary>
    [Fact]
    public void Build_IdentityPromptNull_EmitsDefaultIdentityLineVerbatim()
    {
        string result = SystemPromptBuilder.Build(MakeContext(identityPrompt: null));

        Assert.Contains(DefaultIdentityLine, result, StringComparison.Ordinal);
    }

    /// <summary>With <see cref="QueryContext.IdentityPrompt"/> set, the custom text replaces the default identity line, which no longer appears.</summary>
    [Fact]
    public void Build_IdentityPromptSet_ReplacesDefaultIdentityLine()
    {
        string result = SystemPromptBuilder.Build(MakeContext(identityPrompt: "You are Ada, a code-review specialist."));

        Assert.Contains("You are Ada, a code-review specialist.", result, StringComparison.Ordinal);
        Assert.DoesNotContain(DefaultIdentityLine, result, StringComparison.Ordinal);
    }

    // ── (c): only the identity line changes — no wholesale "Replace mode" ──────

    /// <summary>The ReAct reasoning instruction and grounding sections survive a null <see cref="QueryContext.IdentityPrompt"/>.</summary>
    [Fact]
    public void Build_IdentityPromptNull_ReActAndGroundingSectionsRemain()
    {
        var ctx = MakeContext(identityPrompt: null) with
        {
            Temporal = new TemporalContext { CurrentDateUtc = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero) },
            Environment = new EnvironmentalContext { OperatingSystem = "Windows 11" },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("reasoning", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026", result, StringComparison.Ordinal);
        Assert.Contains("Windows 11", result, StringComparison.Ordinal);
    }

    /// <summary>The ReAct reasoning instruction and grounding sections survive a custom <see cref="QueryContext.IdentityPrompt"/> — replacing the identity line is not a wholesale "Replace mode".</summary>
    [Fact]
    public void Build_IdentityPromptSet_ReActAndGroundingSectionsRemain()
    {
        var ctx = MakeContext(identityPrompt: "You are Ada, a code-review specialist.") with
        {
            Temporal = new TemporalContext { CurrentDateUtc = new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero) },
            Environment = new EnvironmentalContext { OperatingSystem = "Windows 11" },
        };

        string result = SystemPromptBuilder.Build(ctx);

        Assert.Contains("reasoning", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026", result, StringComparison.Ordinal);
        Assert.Contains("Windows 11", result, StringComparison.Ordinal);
    }

    // ── (d): the identity holds on every iteration, not just the first ─────────

    /// <summary>
    /// The system prompt is rebuilt every iteration (spec §9): a two-iteration run has the custom
    /// identity present in both submitted prompts, captured via <see cref="Context.LastLlmRequest"/>
    /// at the start of the second iteration (still holding the first iteration's snapshot) and
    /// again after the run completes (holding the second iteration's snapshot).
    /// </summary>
    [Fact]
    public async Task Run_TwoIterations_IdentityPresentInBothSubmittedPrompts()
    {
        var tool = new FakeTool("calculator", _ => new ToolResult("42"));
        var llm = new FakeChatClient();
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("use-1", "calculator")])])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        });
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "The result is 42.")])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        });

        var capturedPrompts = new List<string>();
        var hooks = new AgentHooks
        {
            OnPreIteration = (c, _) =>
            {
                // Fires before the current iteration's system prompt is built, so LastLlmRequest
                // (if set) still holds the *previous* iteration's snapshot.
                if (c.LastLlmRequest is { } bytes)
                {
                    var snapshot = Assert.IsType<LlmRequestSnapshot>(LlmRequestSnapshotCodec.Deserialize(bytes));
                    capturedPrompts.Add(snapshot.SystemPrompt);
                }

                return Task.CompletedTask;
            },
        };

        var ctx = MakeContext(identityPrompt: "You are Ada, a code-review specialist.") with
        {
            Tools = new ToolContext { Registry = new ToolRegistry([tool]) },
        };
        var agent = new Agent(llm, "test-model", hooks: hooks);

        await foreach (var _ in agent.RunAsync(ctx, TestContext.Current.CancellationToken))
        {
        }

        // Iteration 1's prompt, captured via the hook at the start of iteration 2.
        Assert.Single(capturedPrompts);
        Assert.Contains("You are Ada, a code-review specialist.", capturedPrompts[0], StringComparison.Ordinal);

        // Iteration 2's prompt, held in LastLlmRequest after the run completes.
        LlmRequestSnapshot finalSnapshot = Assert.IsType<LlmRequestSnapshot>(
            LlmRequestSnapshotCodec.Deserialize(Assert.IsType<byte[]>(ctx.LastLlmRequest)));
        Assert.Equal(2, finalSnapshot.Iteration);
        Assert.Contains("You are Ada, a code-review specialist.", finalSnapshot.SystemPrompt, StringComparison.Ordinal);
    }
}
