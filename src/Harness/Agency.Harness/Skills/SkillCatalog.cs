namespace Agency.Harness.Skills;

/// <summary>
/// An immutable, in-memory implementation of <see cref="ISkillCatalog"/> backed by a fixed set of skills.
/// </summary>
internal sealed class SkillCatalog : ISkillCatalog
{
    private readonly List<Skill> _skills;
    private readonly Dictionary<string, Skill> _byName;

    /// <summary>Gets an empty catalog containing no skills.</summary>
    internal static SkillCatalog Empty { get; } = new SkillCatalog([]);

    /// <summary>Initialises the catalog from the provided skills.</summary>
    /// <param name="skills">The skills to include in the catalog.</param>
    internal SkillCatalog(IEnumerable<Skill> skills)
    {
        _skills = skills.ToList();
        _byName = new Dictionary<string, Skill>(_skills.Count, StringComparer.OrdinalIgnoreCase);

        foreach (Skill skill in _skills)
        {
            _byName[skill.Name] = skill;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<Skill> List() => _skills;

    /// <inheritdoc/>
    public Skill? Find(string name)
    {
        return _byName.TryGetValue(name, out Skill? skill) ? skill : null;
    }
}
