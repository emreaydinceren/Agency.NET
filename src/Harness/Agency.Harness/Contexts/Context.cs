using System.Text.Json;
using Agency.Harness.Skills;

namespace Agency.Harness.Contexts;

// ── Pending-turn types (spec §3.3) ────────────────────────────────────────────

/// <summary>
/// Holds the parked state of a mid-batch permission pause: the completed siblings'
/// results and the list of calls waiting for user approval (spec §3.3, §6.1).
/// </summary>
internal sealed class PendingToolBatch
{
    /// <summary>The loop iteration this batch belongs to.</summary>
    internal int Iteration { get; init; }

    /// <summary>
    /// FunctionResultContent entries for the completed siblings, indexed by batch position.
    /// Pended slots are null; they are filled in on resume.
    /// </summary>
    internal FunctionResultContent?[] Results { get; init; } = [];

    /// <summary>Calls that are waiting for a user permission response.</summary>
    internal List<PendingToolCall> Pending { get; init; } = [];

    /// <summary>
    /// ToolInvokedEvent entries for the completed siblings at park time, indexed by batch position.
    /// Pended slots are null; they are filled in on resume to reconstruct the full batch for
    /// <c>OnPostToolBatch</c> (spec §6.3 step 5).
    /// </summary>
    internal ToolInvokedEvent?[] SiblingToolEvents { get; init; } = [];
}

/// <summary>
/// Represents a single tool call that has been pended for user permission (spec §3.3).
/// </summary>
internal sealed record PendingToolCall(
    Guid RequestId,
    int BatchIndex,
    string CallId,
    string ToolName,
    JsonElement Input,           // post-Rewrite — what will execute on approval
    string? KeyValue,
    string ProposedRule,
    PermissionRequestSource Source,
    string? Reason);

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Canonical session state. The wire-format <c>messages[]</c> array is derived from this object on every loop
/// iteration — it is never the source of truth.
/// </summary>
public sealed record Context
{
    /// <summary>Gets the user's query that seeds the conversation.</summary>
    public required QueryContext Query { get; init; }

    /// <summary>
    /// Gets or sets the knowledge context re-injected into the system prompt each iteration.
    /// Settable (not <c>init</c>-only) so lifecycle hooks such as <c>OnSessionStarted</c> can refresh
    /// domain facts mid-session; the rebuilt system prompt picks up the change on the next iteration.
    /// KnowledgeContext is immutable, so to update the knowledge facts you must assign a new instance to this property.
    /// Knowledge updates are additive; the system prompt accumulates all facts ever assigned to this property over the session lifetime.
    /// KnowledgeContext contains a collection string Facts.
    /// </summary>
    public KnowledgeContext Knowledge { get; set; } = KnowledgeContext.Empty;

    /// <summary>
    /// Gets or sets the memory context for this session.
    /// Settable so the retrieval engine can inject retrieved episodic records
    /// in <c>OnPreIteration</c>; the rebuilt system prompt picks up the change on
    /// every iteration.
    /// </summary>
    public MemoryContext Memory { get; set; } = MemoryContext.Empty;

    /// <summary>Gets the tool context providing the registry for this session.</summary>
    public ToolContext Tools { get; init; } = ToolContext.Empty;

    /// <summary>Gets the skill context providing the catalog of available skills for this session.</summary>
    public SkillContext Skills { get; init; } = SkillContext.Empty;

    /// <summary>
    /// Gets or sets the current retrieval focus. Set by <c>SetFocusTool</c> to bias retrieval
    /// toward a particular task domain without forcing exact-match filtering (Spec §6.7.1).
    /// </summary>
    public FocusContext Focus { get; set; } = FocusContext.Empty;

    /// <summary>
    /// Gets or sets the stable session identity. Set once by the agent loop on the first turn
    /// and reused across subsequent turns so the session id is stable for the lifetime of the
    /// <see cref="Context"/> (Spec P3).
    /// </summary>
    public SessionContext Session { get; set; } = SessionContext.Empty;

    /// <summary>Gets user-specific context injected into the system prompt.</summary>
    public UserSpecificContext User { get; init; } = UserSpecificContext.Empty;

    /// <summary>Gets temporal context injected into the system prompt.</summary>
    public TemporalContext Temporal { get; init; } = TemporalContext.Empty;

    /// <summary>Gets environmental context injected into the system prompt.</summary>
    public EnvironmentalContext Environment { get; init; } = EnvironmentalContext.Empty;

    /// <summary>Gets the conversation manager owning the message history for this session.</summary>
    public IConversationManager Conversation { get; init; } = new InMemoryConversationManager();

    // ── Loop-owned mutable state ─────────────────────────────────────────────

    /// <summary>Gets the number of completed LLM iterations.</summary>
    public int IterationCount { get; internal set; }

    /// <summary>Gets the accumulated USD cost for this session.</summary>
    public decimal TotalCostUsd { get; internal set; }

    /// <summary>Gets the accumulated token usage for this session.</summary>
    public LlmTokenUsage TotalUsage { get; internal set; } = new(0, 0);

    // ── Memory retrieval state ────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the UTC timestamp of the most recent retrieval pass for this session,
    /// or <see langword="null"/> if retrieval has not yet been performed.
    /// Written by the retrieval engine in <c>OnPreIteration</c>; read by the retrieval gate
    /// (Spec §8.1) to decide whether to skip a redundant search.
    /// </summary>
    public DateTimeOffset? MemoryLastRetrievedAt { get; set; }

    // ── Permission park state ─────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the pending tool batch when the turn is parked awaiting user permission
    /// (<see cref="AgentResultStatus.AwaitingPermission"/>). Null when no turn is parked.
    /// Cleared on resume or abandonment. This is the serialization target for the harness
    /// state-persistence project (spec §6.6).
    /// </summary>
    internal PendingToolBatch? PendingToolBatch { get; set; }

    // ── Active-skill permission state ─────────────────────────────────────────

    /// <summary>
    /// Gets the mutable active-skill state that tracks which tools are currently pre-approved
    /// by the most recently invoked skill. Set by <see cref="Skills.SkillTool"/> on successful
    /// invocation; cleared by the agent loop at the start of each user turn.
    /// </summary>
    internal ActiveSkillState ActiveSkillState { get; } = new();
}
