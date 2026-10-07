namespace Agency.Indexer;

/// <summary>Writes the embedded <c>SKILL.md</c> and <c>REFERENCE.md</c> into agent skill folders.</summary>
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

    /// <summary>The embedded files of the skill: the short entry point and the reference loaded on demand.</summary>
    private static readonly string[] Files = ["SKILL.md", "REFERENCE.md"];

    /// <summary>Writes <c>&lt;root&gt;/agency-index/SKILL.md</c> and <c>REFERENCE.md</c> under each of <paramref name="roots"/>.</summary>
    /// <returns>The paths written.</returns>
    public static async Task<IReadOnlyList<string>> InstallAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var contents = new List<(string Name, string Content)>();
        foreach (string file in Files)
        {
            contents.Add((file, await ReadResourceAsync(file, ct)));
        }

        var written = new List<string>();
        foreach (string root in roots)
        {
            string directory = Path.Combine(Path.GetFullPath(root), SkillName);
            Directory.CreateDirectory(directory);
            foreach (var (name, content) in contents)
            {
                string path = Path.Combine(directory, name);
                await File.WriteAllTextAsync(path, content, ct);
                written.Add(path);
            }
        }

        return written;
    }

    /// <summary>Reads the embedded resource <paramref name="name"/> (<c>SKILL.md</c> or <c>REFERENCE.md</c>).</summary>
    public static async Task<string> ReadResourceAsync(string name, CancellationToken ct)
    {
        await using Stream stream = typeof(SkillInstaller).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in the assembly.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
