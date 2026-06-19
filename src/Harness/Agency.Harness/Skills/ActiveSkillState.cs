namespace Agency.Harness.Skills;

/// <summary>
/// Tracks the set of tools pre-approved by the most recently invoked skill.
/// A skill becomes "active" when <see cref="SkillTool.InvokeAsync"/> returns a non-error result,
/// and its active state is cleared on the next user message (in <see cref="Agent.ChatAsync"/>).
/// </summary>
/// <remarks>
/// This object is owned by <see cref="Contexts.Context"/> and shared with <see cref="SkillTool"/>
/// so the tool can record which tools are pre-approved without coupling the agent loop to the
/// skill domain model. Mutation is single-threaded within a session.
/// </remarks>
internal sealed class ActiveSkillState
{
    /// <summary>
    /// Gets the tool names currently pre-approved by the active skill.
    /// Empty when no skill is active or the active skill declared no <c>allowed-tools</c>.
    /// </summary>
    internal IReadOnlyList<string> AllowedTools { get; private set; } = [];

    /// <summary>
    /// Records that a skill with the given <paramref name="allowedTools"/> is now active.
    /// Replaces any previously active skill's allowed-tools list.
    /// </summary>
    /// <param name="allowedTools">The tool names pre-approved by the newly active skill.</param>
    internal void Set(IReadOnlyList<string> allowedTools)
    {
        this.AllowedTools = allowedTools;
    }

    /// <summary>
    /// Clears the active skill state. Called when the next user message arrives so the
    /// pre-approval window is bounded to the single turn in which the skill was invoked.
    /// </summary>
    internal void Clear()
    {
        this.AllowedTools = [];
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="toolName"/> is in the current
    /// allowed-tools list (exact-match, OrdinalIgnoreCase).
    /// </summary>
    /// <param name="toolName">The tool name to test.</param>
    internal bool IsAllowed(string toolName) =>
        this.AllowedTools.Count > 0 &&
        this.AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
}
