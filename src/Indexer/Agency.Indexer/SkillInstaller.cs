namespace Agency.Indexer;

/// <summary>A skill file written by <see cref="SkillInstaller.InstallAsync"/>.</summary>
/// <param name="Path">The full path of the written <c>SKILL.md</c>.</param>
/// <param name="Replaced">Whether a <c>SKILL.md</c> already existed there and was overwritten.</param>
internal sealed record InstalledSkill(string Path, bool Replaced);

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

    /// <summary>
    /// Resolves the skill roots a command targets: an explicit <paramref name="dir"/>, else the folders of
    /// <paramref name="scope"/> (<c>repo</c>: the working directory's <c>.claude/skills</c>; <c>user</c>: <see cref="DefaultRoots"/>).
    /// </summary>
    public static IReadOnlyList<string> ResolveRoots(string? dir, string? scope, string userProfile, string workingDirectory) =>
        dir is not null
            ? [dir]
            : scope switch
            {
                "repo" => [Path.Combine(workingDirectory, ".claude", "skills")],
                "user" => DefaultRoots(userProfile),
                _ => throw new UsageException($"Unknown scope '{scope}'. Expected 'repo' or 'user'."),
            };

    /// <summary>Writes <c>&lt;root&gt;/agency-index/SKILL.md</c> under each of <paramref name="roots"/>.</summary>
    /// <returns>The files written.</returns>
    public static async Task<IReadOnlyList<InstalledSkill>> InstallAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        string content = await ReadSkillAsync(ct);
        var written = new List<InstalledSkill>();
        foreach (string root in roots)
        {
            string directory = Path.Combine(Path.GetFullPath(root), SkillName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "SKILL.md");
            bool replaced = File.Exists(path);
            await File.WriteAllTextAsync(path, content, ct);
            written.Add(new InstalledSkill(path, replaced));
        }

        return written;
    }

    /// <summary>Deletes <c>&lt;root&gt;/agency-index/SKILL.md</c> under each of <paramref name="roots"/>, and its folder if that leaves it empty.</summary>
    /// <returns>The files deleted.</returns>
    public static IReadOnlyList<string> Uninstall(IReadOnlyList<string> roots)
    {
        var removed = new List<string>();
        foreach (string root in roots)
        {
            string directory = Path.Combine(Path.GetFullPath(root), SkillName);
            string path = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
            removed.Add(path);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        return removed;
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
