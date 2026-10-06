namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="SkillInstaller"/>.</summary>
public sealed class SkillInstallerTests
{
    /// <summary>Verifies the embedded skill is written under every root with valid front matter.</summary>
    [Fact]
    public async Task InstallAsync_WritesSkillUnderEachRoot()
    {
        using var dir = new TempDirectory();
        string[] roots = [Path.Combine(dir.Path, "a"), Path.Combine(dir.Path, "b")];

        IReadOnlyList<InstalledSkill> written = await SkillInstaller.InstallAsync(roots, TestContext.Current.CancellationToken);

        Assert.Equal(2, written.Count);
        foreach (InstalledSkill skill in written)
        {
            Assert.False(skill.Replaced);
            Assert.Equal("agency-index", Path.GetFileName(Path.GetDirectoryName(skill.Path)));
            string content = await File.ReadAllTextAsync(skill.Path, TestContext.Current.CancellationToken);
            Assert.StartsWith("---\nname: agency-index\ndescription: ", content.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies installing over an existing skill reports that it was replaced.</summary>
    [Fact]
    public async Task InstallAsync_ExistingSkill_ReportsReplaced()
    {
        using var dir = new TempDirectory();

        await SkillInstaller.InstallAsync([dir.Path], TestContext.Current.CancellationToken);
        IReadOnlyList<InstalledSkill> second = await SkillInstaller.InstallAsync([dir.Path], TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(second).Replaced);
    }

    /// <summary>Verifies uninstall removes the skill file and its emptied folder, and ignores roots without it.</summary>
    [Fact]
    public async Task Uninstall_RemovesSkillAndEmptyFolder()
    {
        using var dir = new TempDirectory();
        string installed = Path.Combine(dir.Path, "a");
        await SkillInstaller.InstallAsync([installed], TestContext.Current.CancellationToken);

        IReadOnlyList<string> removed = SkillInstaller.Uninstall([installed, Path.Combine(dir.Path, "missing")]);

        Assert.Single(removed);
        Assert.False(Directory.Exists(Path.Combine(installed, SkillInstaller.SkillName)));
    }

    /// <summary>Verifies the repo scope resolves to the working directory and an unknown scope is a usage error.</summary>
    [Fact]
    public void ResolveRoots_ScopeAndDir_ResolveAsDocumented()
    {
        Assert.Equal([Path.Combine("w", ".claude", "skills")], SkillInstaller.ResolveRoots(null, "repo", "u", "w"));
        Assert.Equal(["custom"], SkillInstaller.ResolveRoots("custom", "repo", "u", "w"));
        Assert.Equal(2, SkillInstaller.ResolveRoots(null, "user", "u", "w").Count);
        Assert.Throws<UsageException>(() => SkillInstaller.ResolveRoots(null, "global", "u", "w"));
    }
}
