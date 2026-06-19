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

    public SkillLoaderTests()
    {
        this._root = Path.Combine(Path.GetTempPath(), "SkillLoaderTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(this._root);
    }

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

    [Fact]
    public void Load_NonExistentRoot_IsSkippedGracefully()
    {
        string nonExistent = Path.Combine(this._root, "does-not-exist");
        CreateSkill(this._root, "real-skill");

        SkillCatalog catalog = SkillLoader.Load([nonExistent, this._root]);

        Assert.Single(catalog.List());
        Assert.NotNull(catalog.Find("real-skill"));
    }

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

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        CreateSkill(this._root, "known-skill");

        SkillCatalog catalog = SkillLoader.Load([this._root]);

        Assert.Null(catalog.Find("unknown-skill"));
    }

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

    [Fact]
    public void Empty_ReturnsEmptyCatalog()
    {
        ISkillCatalog empty = SkillCatalog.Empty;

        Assert.Empty(empty.List());
        Assert.Null(empty.Find("anything"));
    }
}
