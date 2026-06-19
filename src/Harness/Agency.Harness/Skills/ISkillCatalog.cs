namespace Agency.Harness.Skills;

/// <summary>
/// Provides read-only access to the discovered set of skills.
/// </summary>
internal interface ISkillCatalog
{
    /// <summary>Returns all skills in the catalog.</summary>
    IReadOnlyList<Skill> List();

    /// <summary>
    /// Finds a skill by its canonical name (the directory name / invocation key).
    /// Returns <see langword="null"/> when no skill with that name exists.
    /// </summary>
    /// <param name="name">The canonical skill name (directory name).</param>
    Skill? Find(string name);
}
