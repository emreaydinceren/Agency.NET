using Agency.Harness.Skills;

namespace Agency.Harness.Test.Skills;

/// <summary>
/// Tests for <see cref="SkillLoader"/> and <see cref="SkillCatalog"/>.
/// </summary>
public sealed class SkillLoaderTests : IDisposable
{
    // ---------------------------------------------------------------------------
    // Temp directory scaffolding
    // ---------------------------------------------------------------------------

    private readonly string _root;

    /// <summary>Creates a fresh temporary directory to act as a skill root for the test.</summary>
    public SkillLoaderTests()
    {
        this._root = Path.Combine(Path.GetTempPath(), "SkillLoaderTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(this._root);
    }

    /// <summary>Deletes the temporary skill root created for the test, best-effort.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(this._root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>Creates a skill directory with a minimal SKILL.md under the given root.</summary>
    private string CreateSkill(string root, string dirName, string description = "A test skill", string? body = null)
    {
        string skillDir = Path.Combine(root, dirName);
        Directory.CreateDirectory(skillDir);

        string frontmatter = $"---\r\ndescription: {description}\r\n---\r\n{body ?? "Skill body text."}";
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), frontmatter);

        return skillDir;
    }

    // ---------------------------------------------------------------------------
    // Discovery
    // ---------------------------------------------------------------------------

    /// <summary>Loading a single root containing two skill directories discovers both skills.</summary>
    [Fact]
    public void Load_SingleRoot_DiscoversBothSkills()
    {
        CreateSkill(this._root, "skill-a", "Alpha");
        CreateSkill(this._root, "skill-b", "Beta");

        SkillCatalog catalog = SkillLoader.Load([this._root]);

        Assert.Equal(2, catalog.List().Count);
        Assert.NotNull(catalog.Find("skill-a"));
        Assert.NotNull(catalog.Find("skill-b"));
    }

    /// <summary>Loading a skill directory populates <see cref="Skill.Name"/>, <see cref="Skill.Description"/>, and <see cref="Skill.SkillDir"/> from its SKILL.md and location.</summary>
    [Fact]
    public void Load_SkillDir_PopulatesFields()
    {
        string skillDir = CreateSkill(this._root, "my-skill", "My description", "My body.");

        SkillCatalog catalog = SkillLoader.Load([this._root]);
        Skill? skill = catalog.Find("my-skill");

        Assert.NotNull(skill);
        Assert.Equal("my-skill", skill.Name);
        Assert.Equal("My description", skill.Description);
        Assert.Equal(skillDir, skill.SkillDir);
    }

    // ---------------------------------------------------------------------------
    // Precedence — first root wins
    // ---------------------------------------------------------------------------

    /// <summary>When the same skill name exists in two roots, the skill from the first root passed to <c>Load</c> wins.</summary>
    [Fact]
    public void Load_SameSkillInTwoRoots_FirstRootWins()
    {
        string root1 = Path.Combine(this._root, "project");
        string root2 = Path.Combine(this._root, "personal");
        Directory.CreateDirectory(root1);
        Directory.CreateDirectory(root2);

        CreateSkill(root1, "shared-skill", description: "Project version");
        CreateSkill(root2, "shared-skill", description: "Personal version");

        // root1 is passed first → project overrides personal.
        SkillCatalog catalog = SkillLoader.Load([root1, root2]);

        Skill? skill = catalog.Find("shared-skill");
        Assert.NotNull(skill);
        Assert.Equal("Project version", skill.Description);
    }

    /// <summary>When the same skill name exists in two roots, the resulting catalog contains only one entry for that name.</summary>
    [Fact]
    public void Load_SameSkillInTwoRoots_OnlyOneEntryInList()
    {
        string root1 = Path.Combine(this._root, "project");
        string root2 = Path.Combine(this._root, "personal");
        Directory.CreateDirectory(root1);
        Directory.CreateDirectory(root2);

        CreateSkill(root1, "shared-skill", description: "Project version");
        CreateSkill(root2, "shared-skill", description: "Personal version");
        CreateSkill(root2, "personal-only", description: "Personal only");

        SkillCatalog catalog = SkillLoader.Load([root1, root2]);

        Assert.Equal(2, catalog.List().Count);
    }

    // ---------------------------------------------------------------------------
    // Skipping — no SKILL.md
    // ---------------------------------------------------------------------------

    /// <summary>A directory without a SKILL.md file is skipped, while a valid sibling skill directory is still loaded.</summary>
    [Fact]
    public void Load_DirWithoutSkillMd_IsSkipped()
    {
        // A dir without SKILL.md.
        Directory.CreateDirectory(Path.Combine(this._root, "empty-dir"));
        // A valid skill alongside it.
        CreateSkill(this._root, "valid-skill");

        SkillCatalog catalog = SkillLoader.Load([this._root]);

        Assert.Single(catalog.List());
        Assert.NotNull(catalog.Find("valid-skill"));
        Assert.Null(catalog.Find("empty-dir"));
    }

    // ---------------------------------------------------------------------------
    // Skipping — non-existent root
    // ---------------------------------------------------------------------------

    /// <summary>A non-existent root is skipped without throwing, while a valid root passed alongside it is still loaded.</summary>
    [Fact]
    public void Load_NonExistentRoot_IsSkippedGracefully()
    {
        string nonExistent = Path.Combine(this._root, "does-not-exist");
        CreateSkill(this._root, "real-skill");

        SkillCatalog catalog = SkillLoader.Load([nonExistent, this._root]);

        Assert.Single(catalog.List());
        Assert.NotNull(catalog.Find("real-skill"));
    }

    /// <summary>When every root passed to <c>Load</c> is non-existent, the result is an empty catalog rather than a thrown exception.</summary>
    [Fact]
    public void Load_AllRootsNonExistent_ReturnsEmptyCatalog()
    {
        string nonExistent = Path.Combine(this._root, "does-not-exist");

        SkillCatalog catalog = SkillLoader.Load([nonExistent]);

        Assert.Empty(catalog.List());
    }

    // ---------------------------------------------------------------------------
    // SkillCatalog.Find
    // ---------------------------------------------------------------------------

    /// <summary>Looking up a skill name that is not in the catalog returns <see langword="null"/>.</summary>
    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        CreateSkill(this._root, "known-skill");

        SkillCatalog catalog = SkillLoader.Load([this._root]);

        Assert.Null(catalog.Find("unknown-skill"));
    }

    /// <summary>Looking up a skill by name matches regardless of the casing used in the lookup.</summary>
    [Fact]
    public void Find_IsCaseInsensitive()
    {
        CreateSkill(this._root, "my-skill");

        SkillCatalog catalog = SkillLoader.Load([this._root]);

        Assert.NotNull(catalog.Find("MY-SKILL"));
        Assert.NotNull(catalog.Find("My-Skill"));
    }

    // ---------------------------------------------------------------------------
    // SkillCatalog.Empty
    // ---------------------------------------------------------------------------

    /// <summary><see cref="SkillCatalog.Empty"/> exposes a catalog with no entries, whose lookups all return <see langword="null"/>.</summary>
    [Fact]
    public void Empty_ReturnsEmptyCatalog()
    {
        ISkillCatalog empty = SkillCatalog.Empty;

        Assert.Empty(empty.List());
        Assert.Null(empty.Find("anything"));
    }
}
