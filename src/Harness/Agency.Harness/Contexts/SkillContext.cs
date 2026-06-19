using Agency.Harness.Skills;

namespace Agency.Harness.Contexts;

/// <summary>
/// Wraps a live <see cref="ISkillCatalog"/> reference so the system prompt always
/// reflects the current catalog state (supports Phase-2 live reload without
/// needing to re-build the <see cref="Context"/>).
/// </summary>
public sealed record SkillContext
{
    /// <summary>Gets the shared empty skill context (no skills).</summary>
    internal static SkillContext Empty { get; } = new();

    /// <summary>Gets the live catalog of discovered skills.</summary>
    internal ISkillCatalog Catalog { get; init; } = SkillCatalog.Empty;

    /// <summary>Returns all skills in the catalog.</summary>
    internal IReadOnlyList<Skill> List() => this.Catalog.List();

    /// <summary>
    /// Finds a skill by its canonical name (the directory name / invocation key).
    /// Returns <see langword="null"/> when no skill with that name exists.
    /// </summary>
    /// <param name="name">The canonical skill name (directory name).</param>
    internal Skill? Find(string name) => this.Catalog.Find(name);
}
