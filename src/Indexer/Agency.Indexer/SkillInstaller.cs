namespace Agency.Indexer;

/// <summary>A skill file written by <see cref="SkillInstaller.InstallAsync"/>.</summary>
/// <param name="Path">The full path of the written <c>SKILL.md</c>.</param>
/// <param name="Replaced">Whether a <c>SKILL.md</c> already existed there and was overwritten.</param>
internal sealed record InstalledSkill(string Path, bool Replaced);

/// <summary>Writes the embedded <c>SKILL.md</c> and <c>REFERENCE.md</c> into agent skill folders.</summary>
internal static class SkillInstaller
{
    /// <summary>The skill's folder name.</summary>
    public const string SkillName = "agency-index";

    /// <summary>The files of the skill: the short entry point, then the reference loaded on demand.</summary>
    private static readonly string[] Files = ["SKILL.md", "REFERENCE.md"];

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

    /// <summary>Writes <c>&lt;root&gt;/agency-index/SKILL.md</c> and <c>REFERENCE.md</c> under each of <paramref name="roots"/>.</summary>
    /// <returns>The <c>SKILL.md</c> written under each root.</returns>
    public static async Task<IReadOnlyList<InstalledSkill>> InstallAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var contents = new List<(string Name, string Content)>();
        foreach (string file in Files)
        {
            contents.Add((file, await ReadResourceAsync(file, ct)));
        }

        var written = new List<InstalledSkill>();
        foreach (string root in roots)
        {
            string directory = Path.Combine(Path.GetFullPath(root), SkillName);
            Directory.CreateDirectory(directory);
            foreach (var (name, content) in contents)
            {
                string path = Path.Combine(directory, name);
                bool replaced = File.Exists(path);
                await File.WriteAllTextAsync(path, content, ct);
                if (name == "SKILL.md")
                {
                    written.Add(new InstalledSkill(path, replaced));
                }
            }
        }

        return written;
    }

    /// <summary>Deletes <c>&lt;root&gt;/agency-index/SKILL.md</c> and <c>REFERENCE.md</c> under each of <paramref name="roots"/>, and the folder if that leaves it empty.</summary>
    /// <returns>The <c>SKILL.md</c> files deleted.</returns>
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

            foreach (string file in Files)
            {
                File.Delete(Path.Combine(directory, file));
            }

            removed.Add(path);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        return removed;
    }

    /// <summary>Reads the <c>SKILL.md</c> embedded in this assembly.</summary>
    public static Task<string> ReadSkillAsync(CancellationToken ct) => ReadResourceAsync("SKILL.md", ct);

    /// <summary>Reads the embedded resource <paramref name="name"/> (<c>SKILL.md</c> or <c>REFERENCE.md</c>).</summary>
    public static async Task<string> ReadResourceAsync(string name, CancellationToken ct)
    {
        await using Stream stream = typeof(SkillInstaller).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
