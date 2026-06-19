namespace Agency.Harness.Skills;

/// <summary>
/// Discovers and loads skills from one or more root directories.
/// Each root is expected to contain immediate subdirectories, each holding a <c>SKILL.md</c> file.
/// </summary>
internal static class SkillLoader
{
    private const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Scans <paramref name="roots"/> in order and returns a <see cref="SkillCatalog"/> containing all
    /// discovered skills. When two roots contain a skill with the same name, the one from the
    /// earlier root wins (first-occurrence precedence). The caller is responsible for ordering
    /// roots by precedence (project-first, then personal, etc.).
    /// </summary>
    /// <param name="roots">
    /// Ordered collection of root directory paths to scan. Paths that do not exist on disk are
    /// silently skipped. Subdirectories without a <c>SKILL.md</c> are ignored.
    /// </param>
    /// <returns>A <see cref="SkillCatalog"/> populated with all discovered skills.</returns>
    internal static SkillCatalog Load(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skills = new List<Skill>();

        foreach (string root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string skillDir in Directory.EnumerateDirectories(root))
            {
                string dirName = Path.GetFileName(skillDir);
                if (!seen.Add(dirName))
                {
                    // A higher-precedence root already provided a skill with this name.
                    continue;
                }

                string skillFile = Path.Combine(skillDir, SkillFileName);
                if (!File.Exists(skillFile))
                {
                    continue;
                }

                try
                {
                    string text = File.ReadAllText(skillFile);
                    Skill skill = SkillParser.Parse(text, skillDir, dirName);
                    skills.Add(skill);
                }
                catch (Exception)
                {
                    // Malformed or unreadable skill — skip gracefully.
                }
            }
        }

        return new SkillCatalog(skills);
    }
}
