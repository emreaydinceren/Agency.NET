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

        Assert.Equal(2, written.Count);
        foreach (string path in written)
        {
            Assert.Equal("agency-index", Path.GetFileName(Path.GetDirectoryName(path)));
            string content = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.StartsWith("---\nname: agency-index\ndescription: ", content.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        }
    }
}
