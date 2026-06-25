using Agency.Harness.Skills;

namespace Agency.Harness.Test.Skills;

/// <summary>
/// T-SKILL-1 — verifies that the shipped Loop Kit skills (<c>plan</c> and <c>refactor-loop</c>)
/// parse correctly and that <c>refactor-loop</c> declares the required <c>AllowedTools</c>.
/// </summary>
public sealed class LoopSkillParseTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Resolves the repository root by walking up from the test-assembly output directory
    /// until a directory whose child <c>src/Agency.slnx</c> exists.
    /// </summary>
    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "src", "Agency.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "Cannot locate the repository root (no src/Agency.slnx found above " + AppContext.BaseDirectory + ").");
    }

    /// <summary>Reads and parses a shipped skill by directory name from <c>.agency/skills/</c>.</summary>
    private static Skill LoadShippedSkill(string skillName)
    {
        string repoRoot = FindRepoRoot();
        string skillDir = Path.Combine(repoRoot, ".agency", "skills", skillName);
        string skillFile = Path.Combine(skillDir, "SKILL.md");

        Assert.True(File.Exists(skillFile),
            $"Expected shipped SKILL.md at: {skillFile}");

        string text = File.ReadAllText(skillFile);
        return SkillParser.Parse(text, skillDir, skillName);
    }

    // ---------------------------------------------------------------------------
    // T-SKILL-1 — plan skill
    // ---------------------------------------------------------------------------

    [Fact]
    public void Plan_Skill_ParsesWithDescriptionAndBody()
    {
        Skill skill = LoadShippedSkill("plan");

        Assert.Equal("plan", skill.Name);
        Assert.False(string.IsNullOrWhiteSpace(skill.Description),
            "plan skill must have a non-empty description.");
        Assert.False(string.IsNullOrWhiteSpace(skill.Body),
            "plan skill must have a non-empty body.");
    }

    // ---------------------------------------------------------------------------
    // T-SKILL-1 — refactor-loop skill
    // ---------------------------------------------------------------------------

    [Fact]
    public void RefactorLoop_Skill_ParsesWithDescriptionAndBody()
    {
        Skill skill = LoadShippedSkill("refactor-loop");

        Assert.Equal("refactor-loop", skill.Name);
        Assert.False(string.IsNullOrWhiteSpace(skill.Description),
            "refactor-loop skill must have a non-empty description.");
        Assert.False(string.IsNullOrWhiteSpace(skill.Body),
            "refactor-loop skill must have a non-empty body.");
    }

    [Fact]
    public void RefactorLoop_Skill_AllowedTools_ContainsEnableGoalkeeper()
    {
        Skill skill = LoadShippedSkill("refactor-loop");

        Assert.Contains("enable_goalkeeper", skill.AllowedTools, StringComparer.Ordinal);
    }

    [Fact]
    public void RefactorLoop_Skill_AllowedTools_ContainsDisableGoalkeeper()
    {
        Skill skill = LoadShippedSkill("refactor-loop");

        Assert.Contains("disable_goalkeeper", skill.AllowedTools, StringComparer.Ordinal);
    }

    [Fact]
    public void RefactorLoop_Skill_AllowedTools_ContainsSubagentTool()
    {
        Skill skill = LoadShippedSkill("refactor-loop");

        Assert.Contains("subagent_tool", skill.AllowedTools, StringComparer.Ordinal);
    }
}
