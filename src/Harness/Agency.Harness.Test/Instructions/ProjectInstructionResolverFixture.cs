using Agency.Harness.Instructions;

namespace Agency.Harness.Test.Instructions;

/// <summary>Tests for <see cref="ProjectInstructionResolver"/>.</summary>
public sealed class ProjectInstructionResolverTests : IDisposable
{
    private readonly string _tempDir;

    /// <summary>Initializes a new instance of the <see cref="ProjectInstructionResolverTests"/> class.</summary>
    public ProjectInstructionResolverTests()
    {
        this._tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    }

    /// <summary>Cleans up the temporary directory.</summary>
    public void Dispose()
    {
        if (Directory.Exists(this._tempDir))
        {
            Directory.Delete(this._tempDir, recursive: true);
        }
    }

    /// <summary>Verifies that a single Agents.md at repo root is discovered and marked correctly.</summary>
    [Fact]
    public async Task ResolveAsync_WithAgentsMdInRepoRoot_ReturnsRepoRootSource()
    {
        // Arrange: create a .git directory and Agents.md file
        Directory.CreateDirectory(this._tempDir);
        Directory.CreateDirectory(Path.Combine(this._tempDir, ".git"));
        var agentsPath = Path.Combine(this._tempDir, "Agents.md");
        var testContent = "# Test Instructions\n\nThis is test content.";
        await File.WriteAllTextAsync(agentsPath, testContent);

        var resolver = new ProjectInstructionResolver();

        // Act
        var ctx = await resolver.ResolveAsync(this._tempDir);

        // Assert
        Assert.Single(ctx.Sources);
        Assert.Equal(InstructionSourceKind.RepoRoot, ctx.Sources[0].Kind);
        Assert.Equal(testContent, ctx.Sources[0].Content);
        Assert.True(ctx.HasSources);
    }

    /// <summary>Verifies that multiple Agents.md files are returned in root-first order.</summary>
    [Fact]
    public async Task ResolveAsync_WithAgentsMdInAncestorAndRoot_ReturnsInOrder()
    {
        // Arrange: create nested directories with .git at root
        Directory.CreateDirectory(this._tempDir);
        Directory.CreateDirectory(Path.Combine(this._tempDir, ".git"));

        var ancestorPath = Path.Combine(this._tempDir, "Agents.md");
        var ancestorContent = "# Root Level";
        await File.WriteAllTextAsync(ancestorPath, ancestorContent);

        var subdir = Path.Combine(this._tempDir, "subdir");
        Directory.CreateDirectory(subdir);
        var subPath = Path.Combine(subdir, "Agents.md");
        var subContent = "# Sub Level";
        await File.WriteAllTextAsync(subPath, subContent);

        var resolver = new ProjectInstructionResolver();

        // Act
        var ctx = await resolver.ResolveAsync(subdir);

        // Assert: root-first ordering
        Assert.Equal(2, ctx.Sources.Count);
        Assert.Equal(InstructionSourceKind.RepoRoot, ctx.Sources[0].Kind);
        Assert.Equal(ancestorContent, ctx.Sources[0].Content);
        Assert.Equal(InstructionSourceKind.Ancestor, ctx.Sources[1].Kind);
        Assert.Equal(subContent, ctx.Sources[1].Content);
    }
}
