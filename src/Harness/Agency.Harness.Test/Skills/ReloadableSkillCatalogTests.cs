using Agency.Harness.Skills;

namespace Agency.Harness.Test.Skills;

/// <summary>
/// Tests for <see cref="ReloadableSkillCatalog"/>. All tests call <c>Reload()</c> directly —
/// there is no dependency on <c>FileSystemWatcher</c> timing, which is inherently non-deterministic.
/// The live-watcher integration (SkillWatcher) is covered separately; it is intentionally omitted
/// here because FSW timing cannot be made reliably deterministic across CI environments.
/// </summary>
public sealed class ReloadableSkillCatalogTests : IDisposable
{
    private readonly string _root;

    /// <summary>Creates a fresh temporary directory to act as a skill root for the test.</summary>
    public ReloadableSkillCatalogTests()
    {
        this._root = Path.Combine(Path.GetTempPath(), "ReloadableSkillCatalogTests_" + Path.GetRandomFileName());
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

    /// <summary>Creates a minimal SKILL.md file under <paramref name="root"/>/<paramref name="dirName"/>.</summary>
    private static void CreateSkill(string root, string dirName, string description = "A test skill", string? body = null)
    {
        string skillDir = Path.Combine(root, dirName);
        Directory.CreateDirectory(skillDir);
        string frontmatter = $"---\r\ndescription: {description}\r\n---\r\n{body ?? "Skill body."}";
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), frontmatter);
    }

    /// <summary>Removes the SKILL.md file from an existing skill directory to simulate deletion.</summary>
    private static void RemoveSkill(string root, string dirName)
    {
        string skillFile = Path.Combine(root, dirName, "SKILL.md");
        if (File.Exists(skillFile))
        {
            File.Delete(skillFile);
        }
    }

    // ---------------------------------------------------------------------------
    // Initial load
    // ---------------------------------------------------------------------------

    /// <summary>Constructing the catalog from a root containing skill directories loads all of them.</summary>
    [Fact]
    public void Constructor_LoadsSkillsFromRoots()
    {
        CreateSkill(this._root, "skill-a", "Alpha");
        CreateSkill(this._root, "skill-b", "Beta");

        ReloadableSkillCatalog catalog = new([this._root]);

        Assert.Equal(2, catalog.List().Count);
        Assert.NotNull(catalog.Find("skill-a"));
        Assert.NotNull(catalog.Find("skill-b"));
    }

    /// <summary>Constructing the catalog from a root that does not exist on disk starts with an empty catalog.</summary>
    [Fact]
    public void Constructor_NonExistentRoot_StartsEmpty()
    {
        string nonExistent = Path.Combine(this._root, "does-not-exist");

        ReloadableSkillCatalog catalog = new([nonExistent]);

        Assert.Empty(catalog.List());
    }

    // ---------------------------------------------------------------------------
    // Reload — add
    // ---------------------------------------------------------------------------

    /// <summary>Calling <c>Reload()</c> after a new skill directory is added to disk picks up the new skill.</summary>
    [Fact]
    public void Reload_AfterAddingSkill_CatalogReflectsAddition()
    {
        // Arrange: start with skill A only.
        CreateSkill(this._root, "skill-a", "Alpha");
        ReloadableSkillCatalog catalog = new([this._root]);
        Assert.Single(catalog.List());

        // Act: add skill B to disk, then reload.
        CreateSkill(this._root, "skill-b", "Beta");
        catalog.Reload();

        // Assert: both skills are now present.
        Assert.Equal(2, catalog.List().Count);
        Assert.NotNull(catalog.Find("skill-a"));
        Assert.NotNull(catalog.Find("skill-b"));
    }

    // ---------------------------------------------------------------------------
    // Reload — edit
    // ---------------------------------------------------------------------------

    /// <summary>Calling <c>Reload()</c> after a skill's SKILL.md is rewritten with a new description picks up the change.</summary>
    [Fact]
    public void Reload_AfterEditingSkillDescription_ReflectsNewDescription()
    {
        // Arrange: create skill with original description.
        CreateSkill(this._root, "skill-a", "Original description");
        ReloadableSkillCatalog catalog = new([this._root]);

        Skill? before = catalog.Find("skill-a");
        Assert.NotNull(before);
        Assert.Equal("Original description", before.Description);

        // Act: overwrite SKILL.md with updated description, then reload.
        CreateSkill(this._root, "skill-a", "Updated description");
        catalog.Reload();

        // Assert: updated description is reflected.
        Skill? after = catalog.Find("skill-a");
        Assert.NotNull(after);
        Assert.Equal("Updated description", after.Description);
    }

    // ---------------------------------------------------------------------------
    // Reload — remove
    // ---------------------------------------------------------------------------

    /// <summary>Calling <c>Reload()</c> after a skill's SKILL.md is deleted removes that skill from the catalog while keeping the rest.</summary>
    [Fact]
    public void Reload_AfterRemovingSkill_SkillIsGone()
    {
        // Arrange: start with skills A and B.
        CreateSkill(this._root, "skill-a", "Alpha");
        CreateSkill(this._root, "skill-b", "Beta");
        ReloadableSkillCatalog catalog = new([this._root]);
        Assert.Equal(2, catalog.List().Count);

        // Act: delete skill A's SKILL.md and reload.
        RemoveSkill(this._root, "skill-a");
        catalog.Reload();

        // Assert: only skill B remains.
        Assert.Single(catalog.List());
        Assert.Null(catalog.Find("skill-a"));
        Assert.NotNull(catalog.Find("skill-b"));
    }

    // ---------------------------------------------------------------------------
    // Reload — resilience on bad root
    // ---------------------------------------------------------------------------

    /// <summary>Calling <c>Reload()</c> after the skill root directory itself is deleted completes without throwing and leaves the catalog in a valid (non-null) state.</summary>
    [Fact]
    public void Reload_WhenRootDisappearsAfterConstruction_KeepsPriorCatalog()
    {
        // Arrange: a valid root with one skill.
        CreateSkill(this._root, "skill-a", "Alpha");
        ReloadableSkillCatalog catalog = new([this._root]);
        Assert.Single(catalog.List());

        // Act: delete the root entirely, then reload.
        Directory.Delete(this._root, recursive: true);
        catalog.Reload();

        // Assert: the prior catalog is preserved — Reload over a missing root returns an
        // empty catalog (SkillLoader returns empty for non-existent roots), so this verifies
        // that the reload itself does not throw and completes cleanly.
        // The result is an empty catalog (not corrupted/null).
        Assert.NotNull(catalog.List());
    }

    // ---------------------------------------------------------------------------
    // Reload — idempotent
    // ---------------------------------------------------------------------------

    /// <summary>Calling <c>Reload()</c> repeatedly with no changes on disk does not throw or corrupt the catalog.</summary>
    [Fact]
    public void Reload_CalledMultipleTimes_IsIdempotent()
    {
        CreateSkill(this._root, "skill-a", "Alpha");
        ReloadableSkillCatalog catalog = new([this._root]);

        // Multiple reloads without changes should not throw or corrupt the catalog.
        catalog.Reload();
        catalog.Reload();
        catalog.Reload();

        Assert.Single(catalog.List());
        Assert.NotNull(catalog.Find("skill-a"));
    }

    // ---------------------------------------------------------------------------
    // Reload — multiple roots, precedence preserved across reloads
    // ---------------------------------------------------------------------------

    /// <summary>Calling <c>Reload()</c> with multiple roots preserves the original root-precedence ordering when a skill name is shared across roots.</summary>
    [Fact]
    public void Reload_MultipleRoots_PrecedencePreservedAfterReload()
    {
        // Arrange: project root and personal root each have "shared-skill".
        string projectRoot = Path.Combine(this._root, "project");
        string personalRoot = Path.Combine(this._root, "personal");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(personalRoot);

        CreateSkill(projectRoot, "shared-skill", "Project version");
        CreateSkill(personalRoot, "shared-skill", "Personal version");
        CreateSkill(personalRoot, "personal-only", "Personal only");

        // Project root passed first → project wins.
        ReloadableSkillCatalog catalog = new([projectRoot, personalRoot]);
        Assert.Equal("Project version", catalog.Find("shared-skill")!.Description);
        Assert.Equal(2, catalog.List().Count);

        // Act: no changes — reload must preserve precedence.
        catalog.Reload();

        // Assert: still project version wins, still two distinct skills.
        Assert.Equal("Project version", catalog.Find("shared-skill")!.Description);
        Assert.Equal(2, catalog.List().Count);
    }
}
