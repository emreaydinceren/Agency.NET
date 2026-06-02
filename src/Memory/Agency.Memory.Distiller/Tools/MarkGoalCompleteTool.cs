using System.Text.Json;
using System.Threading.Channels;
using Agency.Harness.Contexts;
using Agency.Llm.Common.Tools;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Distiller.Services;

namespace Agency.Memory.Distiller.Tools;

/// <summary>
/// Agent tool that enqueues a <see cref="DistillationJob"/> with
/// <see cref="DistillationTrigger.GoalCompletion"/> trigger (Spec §6.7.2).
/// </summary>
/// <remarks>
/// Does NOT stop the agent loop — the agent may continue if the user has follow-ups.
/// The watermark prevents reprocessing of already-distilled turns.
/// This tool is instantiated per session with the <paramref name="userId"/> and
/// <paramref name="sessionId"/> baked in, so there is no ambiguity about which session
/// is being marked complete.
/// </remarks>
internal sealed class MarkGoalCompleteTool : ITool
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "summary": {
                    "type": "string",
                    "description": "Optional brief description of what was accomplished. Attached to the distillation job as a hint."
                }
            }
        }
        """).RootElement;

    private readonly ChannelSessionRegistry _channelRegistry;
    private readonly string _userId;
    private readonly string _sessionId;
    private readonly Func<int> _turnIndexAccessor;

    /// <summary>
    /// Initialises a new <see cref="MarkGoalCompleteTool"/>.
    /// </summary>
    /// <param name="channelRegistry">Registry providing per-session channel writers.</param>
    /// <param name="userId">The user id for this session.</param>
    /// <param name="sessionId">The session id for this session.</param>
    /// <param name="turnIndexAccessor">Returns the current conversation turn count at invocation time.</param>
    internal MarkGoalCompleteTool(
        ChannelSessionRegistry channelRegistry,
        string userId,
        string sessionId,
        Func<int> turnIndexAccessor)
    {
        this._channelRegistry = channelRegistry ?? throw new ArgumentNullException(nameof(channelRegistry));
        this._userId = userId ?? throw new ArgumentNullException(nameof(userId));
        this._sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        this._turnIndexAccessor = turnIndexAccessor ?? throw new ArgumentNullException(nameof(turnIndexAccessor));
    }

    /// <inheritdoc/>
    public ToolDefinition Definition => new(
        Name: "MarkGoalComplete",
        Description: "Signal that the current goal has been achieved. " +
                     "Triggers asynchronous memory distillation of this session's conversation. " +
                     "Does not stop the conversation — you may continue assisting the user.",
        InputSchema: _inputSchema);

    /// <inheritdoc/>
    public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
    {
        string? summary = null;
        if (input.TryGetProperty("summary", out JsonElement summaryEl)
            && summaryEl.ValueKind == JsonValueKind.String)
        {
            summary = summaryEl.GetString();
        }

        int turnIndex = this._turnIndexAccessor();

        ChannelWriter<DistillationJob> writer = this._channelRegistry.GetOrCreateWriter(
            this._userId, this._sessionId);

        var job = new DistillationJob(
            UserId: this._userId,
            SessionId: this._sessionId,
            Trigger: DistillationTrigger.GoalCompletion,
            UpToTurnIndex: turnIndex,
            TriggerSummary: summary);

        writer.TryWrite(job);

        string message = string.IsNullOrWhiteSpace(summary)
            ? "Goal marked as complete. Memory distillation queued."
            : $"Goal marked as complete: {summary}. Memory distillation queued.";

        return Task.FromResult(new ToolResult(message));
    }
}
