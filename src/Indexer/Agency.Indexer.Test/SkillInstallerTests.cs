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

        IReadOnlyList<string> written = await SkillInstaller.InstallAsync(roots, TestContext.Current.CancellationToken);

        Assert.Equal(4, written.Count);
        foreach (string path in written)
        {
            Assert.Equal("agency-index", Path.GetFileName(Path.GetDirectoryName(path)));
        }

        foreach (string path in written.Where(p => Path.GetFileName(p) == "SKILL.md"))
        {
            string content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.StartsWith("---\nname: agency-index\ndescription: ", content.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        }

        Assert.Equal(2, written.Count(p => Path.GetFileName(p) == "REFERENCE.md"));
    }

    /// <summary>Verifies the skill stays short; detail belongs in <c>REFERENCE.md</c>, which agents load on demand.</summary>
    [Fact]
    public async Task SkillMd_StaysUnderBudget()
    {
        string content = await SkillInstaller.ReadResourceAsync("SKILL.md", TestContext.Current.CancellationToken);

        Assert.True(content.Length < 1500, $"SKILL.md is {content.Length} characters.");
    }
}
