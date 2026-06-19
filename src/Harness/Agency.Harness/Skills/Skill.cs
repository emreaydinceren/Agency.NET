namespace Agency.Harness.Skills;

/// <summary>
/// Represents a parsed skill — a directory-based unit of instruction loaded from a <c>SKILL.md</c> file.
/// The canonical invocation key is always <see cref="Name"/> (the directory name).
/// </summary>
internal sealed record Skill
{
    /// <summary>Gets the canonical skill name (the directory name — not the frontmatter <c>name</c> field).</summary>
    public required string Name { get; init; }

    /// <summary>Gets the display description used in the system-prompt catalog.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the optional <c>when_to_use</c> guidance appended to the catalog listing.</summary>
    public string? WhenToUse { get; init; }

    /// <summary>Gets the markdown body of the skill (everything after the frontmatter delimiter).</summary>
    public required string Body { get; init; }

    /// <summary>Gets the absolute path to the directory that contains this skill's <c>SKILL.md</c>.</summary>
    public required string SkillDir { get; init; }

    /// <summary>
    /// Gets a value indicating whether this skill is excluded from the model-visible catalog and refused by <c>SkillTool</c>.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool DisableModelInvocation { get; init; }

    /// <summary>
    /// Gets a value indicating whether this skill appears in the Console <c>/</c> menu.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool UserInvocable { get; init; } = true;

    /// <summary>Gets the ordered list of named positional argument names declared in frontmatter.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets the Console autocomplete hint for this skill's arguments (e.g. <c>"&lt;query&gt;"</c>).
    /// Displayed alongside the skill name in the <c>/</c> command picker. <see langword="null"/> when not declared.
    /// </summary>
    public string? ArgumentHint { get; init; }

    /// <summary>
    /// Gets the shell interpreter requested by this skill for <c>!</c>-injection (e.g. <c>powershell</c>).
    /// <see langword="null"/> means no shell was declared; the host selects the default runner.
    /// </summary>
    public string? Shell { get; init; }

    /// <summary>
    /// Gets the list of tool names that are pre-approved while this skill is active.
    /// Declared via the <c>allowed-tools</c> frontmatter field. Empty when not declared.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Gets the execution context for this skill (e.g. <c>"fork"</c> for subagent delegation).
    /// <see langword="null"/> means inline body return (the default).
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// Gets the optional agent type to use when <see cref="Context"/> is <c>"fork"</c>.
    /// Passed to the fork runner as the subagent type hint. <see langword="null"/> when not declared.
    /// </summary>
    public string? Agent { get; init; }
}
