namespace Agency.Indexer;

/// <summary>Writes the embedded <c>SKILL.md</c> into agent skill folders.</summary>
internal static class SkillInstaller
{
    /// <summary>The skill's folder name.</summary>
    public const string SkillName = "agency-index";

    /// <summary>
    /// The default skill roots: Claude Code's user skills folder and the Agency harness's user skills folder.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots(string userProfile) =>
    [
        Path.Combine(userProfile, ".claude", "skills"),
        Path.Combine(userProfile, "Agents", "skills"),
    ];

    /// <summary>Writes <c>&lt;root&gt;/agency-index/SKILL.md</c> under each of <paramref name="roots"/>.</summary>
    /// <returns>The paths written.</returns>
    public static async Task<IReadOnlyList<string>> InstallAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        string content = await ReadSkillAsync(ct);
        var written = new List<string>();
        foreach (string root in roots)
        {
            string directory = Path.Combine(Path.GetFullPath(root), SkillName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "SKILL.md");
            await File.WriteAllTextAsync(path, content, ct);
            written.Add(path);
        }

        return written;
    }

    /// <summary>Reads the <c>SKILL.md</c> embedded in this assembly.</summary>
    public static async Task<string> ReadSkillAsync(CancellationToken ct)
    {
        await using Stream stream = typeof(SkillInstaller).Assembly.GetManifestResourceStream("SKILL.md")
            ?? throw new InvalidOperationException("SKILL.md is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
