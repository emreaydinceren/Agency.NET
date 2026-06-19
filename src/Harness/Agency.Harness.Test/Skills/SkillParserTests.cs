using Agency.Harness.Skills;

namespace Agency.Harness.Test.Skills;

/// <summary>
/// Tests for <see cref="SkillParser"/>.
/// </summary>
public sealed class SkillParserTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Skill ParseText(string text, string dirName = "my-skill") =>
        SkillParser.Parse(text, "/skills/" + dirName, dirName);

    // ---------------------------------------------------------------------------
    // Frontmatter — present
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_WithFullFrontmatter_PopulatesAllFields()
    {
        string text =
            "---\r\n" +
            "description: Does something useful\r\n" +
            "when_to_use: When you need to do something\r\n" +
            "disable-model-invocation: true\r\n" +
            "user-invocable: false\r\n" +
            "arguments: arg1 arg2\r\n" +
            "---\r\n" +
            "Body text here.";

        Skill skill = ParseText(text, "my-skill");

        Assert.Equal("my-skill", skill.Name);
        Assert.Equal("Does something useful", skill.Description);
        Assert.Equal("When you need to do something", skill.WhenToUse);
        Assert.True(skill.DisableModelInvocation);
        Assert.False(skill.UserInvocable);
        Assert.Equal(["arg1", "arg2"], skill.Arguments);
        Assert.Contains("Body text here.", skill.Body);
    }

    [Fact]
    public void Parse_SkillDir_IsSetFromParameter()
    {
        string text = "---\ndescription: Test\n---\nBody.";

        Skill skill = SkillParser.Parse(text, "/absolute/path/to/my-skill", "my-skill");

        Assert.Equal("/absolute/path/to/my-skill", skill.SkillDir);
    }

    // ---------------------------------------------------------------------------
    // Name is ALWAYS dirName — frontmatter name is ignored
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_FrontmatterNameIsIgnored_DirNameUsedAsCanonicalName()
    {
        string text =
            "---\n" +
            "name: cosmetic-display-name\n" +
            "description: A skill\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text, "actual-dir-name");

        Assert.Equal("actual-dir-name", skill.Name);
    }

    // ---------------------------------------------------------------------------
    // Frontmatter — absent
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_NoFrontmatter_BodyIsWholeText()
    {
        string text = "# My Skill\n\nDoes something.";

        Skill skill = ParseText(text, "no-frontmatter");

        Assert.Contains("My Skill", skill.Body);
        Assert.Equal("no-frontmatter", skill.Name);
    }

    [Fact]
    public void Parse_NoFrontmatter_DescriptionFallsBackToFirstParagraph()
    {
        string text = "First paragraph text.\n\nSecond paragraph.";

        Skill skill = ParseText(text);

        Assert.Equal("First paragraph text.", skill.Description);
    }

    [Fact]
    public void Parse_NoFrontmatter_FirstParagraphIsHeading_StripsHashPrefix()
    {
        string text = "# Skill Title\n\nActual description here.";

        Skill skill = ParseText(text);

        // First paragraph is the heading; its # is stripped.
        Assert.Equal("Skill Title", skill.Description);
    }

    // ---------------------------------------------------------------------------
    // Missing description — fallback to first body paragraph
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_FrontmatterWithNoDescription_FallsBackToFirstBodyParagraph()
    {
        string text =
            "---\n" +
            "when_to_use: Anytime\n" +
            "---\n" +
            "\n" +
            "First body paragraph.\n" +
            "\n" +
            "Second paragraph.";

        Skill skill = ParseText(text);

        Assert.Equal("First body paragraph.", skill.Description);
    }

    // ---------------------------------------------------------------------------
    // Boolean defaults
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_BooleanDefaults_DisableModelInvocationFalse_UserInvocableTrue()
    {
        string text = "---\ndescription: A skill\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.False(skill.DisableModelInvocation);
        Assert.True(skill.UserInvocable);
    }

    [Fact]
    public void Parse_DisableModelInvocationTrue_IsRespected()
    {
        string text = "---\ndescription: A skill\ndisable-model-invocation: true\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.True(skill.DisableModelInvocation);
    }

    [Fact]
    public void Parse_UserInvocableFalse_IsRespected()
    {
        string text = "---\ndescription: A skill\nuser-invocable: false\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.False(skill.UserInvocable);
    }

    // ---------------------------------------------------------------------------
    // Arguments — space-separated syntax
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_Arguments_SpaceSeparated_ParsesCorrectly()
    {
        string text = "---\ndescription: A skill\narguments: alpha beta gamma\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Equal(["alpha", "beta", "gamma"], skill.Arguments);
    }

    // ---------------------------------------------------------------------------
    // Arguments — comma-separated syntax
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_Arguments_CommaSeparated_ParsesCorrectly()
    {
        string text = "---\ndescription: A skill\narguments: alpha, beta, gamma\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Equal(["alpha", "beta", "gamma"], skill.Arguments);
    }

    // ---------------------------------------------------------------------------
    // Arguments — YAML block list syntax
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_Arguments_YamlBlockList_ParsesCorrectly()
    {
        string text =
            "---\n" +
            "description: A skill\n" +
            "arguments:\n" +
            "  - alpha\n" +
            "  - beta\n" +
            "  - gamma\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal(["alpha", "beta", "gamma"], skill.Arguments);
    }

    // ---------------------------------------------------------------------------
    // No arguments — empty list
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_NoArgumentsField_ReturnsEmptyList()
    {
        string text = "---\ndescription: A skill\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Empty(skill.Arguments);
    }

    // ---------------------------------------------------------------------------
    // WhenToUse — absent
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_NoWhenToUse_IsNull()
    {
        string text = "---\ndescription: A skill\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Null(skill.WhenToUse);
    }

    // ---------------------------------------------------------------------------
    // Frontmatter with unclosed delimiter
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_UnclosedFrontmatter_TreatsWholeTextAsBody()
    {
        string text = "---\ndescription: A skill\nBody without closing delimiter.";

        Skill skill = ParseText(text, "unclosed");

        Assert.Contains("description: A skill", skill.Body);
    }

    // ---------------------------------------------------------------------------
    // CRLF line endings
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parse_CrlfLineEndings_ParsedCorrectly()
    {
        string text = "---\r\ndescription: CRLF skill\r\narguments: x y\r\n---\r\nBody content.";

        Skill skill = ParseText(text);

        Assert.Equal("CRLF skill", skill.Description);
        Assert.Equal(["x", "y"], skill.Arguments);
        Assert.Contains("Body content.", skill.Body);
    }

    // ---------------------------------------------------------------------------
    // argument-hint (Phase 2, Task 8)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// When <c>argument-hint</c> is present in frontmatter, the parsed value is exposed
    /// on <see cref="Skill.ArgumentHint"/>.
    /// </summary>
    [Fact]
    public void Parse_ArgumentHint_IsPopulatedFromFrontmatter()
    {
        string text =
            "---\n" +
            "description: A skill\n" +
            "argument-hint: <query>\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal("<query>", skill.ArgumentHint);
    }

    /// <summary>
    /// When <c>argument-hint</c> is absent from frontmatter, <see cref="Skill.ArgumentHint"/>
    /// defaults to <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Parse_NoArgumentHint_IsNull()
    {
        string text = "---\ndescription: A skill\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Null(skill.ArgumentHint);
    }

    /// <summary>
    /// <c>argument-hint</c> is preserved alongside other frontmatter fields.
    /// </summary>
    [Fact]
    public void Parse_ArgumentHint_CoexistsWithOtherFrontmatterFields()
    {
        string text =
            "---\n" +
            "description: Multi-field skill\n" +
            "arguments: query\n" +
            "argument-hint: <query text>\n" +
            "user-invocable: true\n" +
            "---\n" +
            "Run $query.";

        Skill skill = ParseText(text);

        Assert.Equal("Multi-field skill", skill.Description);
        Assert.Equal(["query"], skill.Arguments);
        Assert.Equal("<query text>", skill.ArgumentHint);
        Assert.True(skill.UserInvocable);
    }

    // ---------------------------------------------------------------------------
    // allowed-tools — space-separated syntax (Task 9)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <c>allowed-tools</c> declared as a space-separated inline value is split correctly.
    /// </summary>
    [Fact]
    public void Parse_AllowedTools_SpaceSeparated_ParsesCorrectly()
    {
        string text =
            "---\n" +
            "description: A skill\n" +
            "allowed-tools: ReadFile WriteFile ExecutePowershell\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal(["ReadFile", "WriteFile", "ExecutePowershell"], skill.AllowedTools);
    }

    // ---------------------------------------------------------------------------
    // allowed-tools — comma-separated syntax
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <c>allowed-tools</c> declared as a comma-separated inline value is split and trimmed.
    /// </summary>
    [Fact]
    public void Parse_AllowedTools_CommaSeparated_ParsesCorrectly()
    {
        string text =
            "---\n" +
            "description: A skill\n" +
            "allowed-tools: ReadFile, WriteFile, ExecutePowershell\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal(["ReadFile", "WriteFile", "ExecutePowershell"], skill.AllowedTools);
    }

    // ---------------------------------------------------------------------------
    // allowed-tools — YAML block list syntax
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <c>allowed-tools</c> declared as a YAML block list is parsed item-by-item.
    /// </summary>
    [Fact]
    public void Parse_AllowedTools_YamlBlockList_ParsesCorrectly()
    {
        string text =
            "---\n" +
            "description: A skill\n" +
            "allowed-tools:\n" +
            "  - ReadFile\n" +
            "  - WriteFile\n" +
            "  - ExecutePowershell\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal(["ReadFile", "WriteFile", "ExecutePowershell"], skill.AllowedTools);
    }

    // ---------------------------------------------------------------------------
    // allowed-tools — absent → empty list
    // ---------------------------------------------------------------------------

    /// <summary>
    /// When <c>allowed-tools</c> is absent from frontmatter, <see cref="Skill.AllowedTools"/>
    /// defaults to an empty list.
    /// </summary>
    [Fact]
    public void Parse_NoAllowedToolsField_ReturnsEmptyList()
    {
        string text = "---\ndescription: A skill\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Empty(skill.AllowedTools);
    }

    // ---------------------------------------------------------------------------
    // allowed-tools — coexists with other fields
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <c>allowed-tools</c> is populated correctly when other frontmatter fields are also present.
    /// </summary>
    [Fact]
    public void Parse_AllowedTools_CoexistsWithOtherFrontmatterFields()
    {
        string text =
            "---\n" +
            "description: Multi-field skill\n" +
            "arguments: query\n" +
            "allowed-tools: ReadFile WriteFile\n" +
            "user-invocable: true\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal("Multi-field skill", skill.Description);
        Assert.Equal(["query"], skill.Arguments);
        Assert.Equal(["ReadFile", "WriteFile"], skill.AllowedTools);
        Assert.True(skill.UserInvocable);
    }

    // ---------------------------------------------------------------------------
    // context: fork / agent (Task 10)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <c>context: fork</c> is parsed and exposed on <see cref="Skill.Context"/>.
    /// </summary>
    [Fact]
    public void Parse_ContextFork_IsPopulated()
    {
        string text =
            "---\n" +
            "description: A forking skill\n" +
            "context: fork\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal("fork", skill.Context);
    }

    /// <summary>
    /// When <c>context</c> is absent, <see cref="Skill.Context"/> is <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Parse_NoContext_IsNull()
    {
        string text = "---\ndescription: A skill\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Null(skill.Context);
    }

    /// <summary>
    /// <c>agent</c> frontmatter field is parsed and exposed on <see cref="Skill.Agent"/>.
    /// </summary>
    [Fact]
    public void Parse_Agent_IsPopulatedFromFrontmatter()
    {
        string text =
            "---\n" +
            "description: A forking skill\n" +
            "context: fork\n" +
            "agent: code-reviewer\n" +
            "---\n" +
            "Body.";

        Skill skill = ParseText(text);

        Assert.Equal("code-reviewer", skill.Agent);
    }

    /// <summary>
    /// When <c>agent</c> is absent, <see cref="Skill.Agent"/> is <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Parse_NoAgent_IsNull()
    {
        string text = "---\ndescription: A skill\n---\nBody.";

        Skill skill = ParseText(text);

        Assert.Null(skill.Agent);
    }

    /// <summary>
    /// <c>context</c> and <c>agent</c> coexist correctly with other frontmatter fields.
    /// </summary>
    [Fact]
    public void Parse_ContextAndAgent_CoexistWithOtherFields()
    {
        string text =
            "---\n" +
            "description: Full fork skill\n" +
            "context: fork\n" +
            "agent: deep-research\n" +
            "arguments: query\n" +
            "user-invocable: true\n" +
            "---\n" +
            "Research $query.";

        Skill skill = ParseText(text);

        Assert.Equal("Full fork skill", skill.Description);
        Assert.Equal("fork", skill.Context);
        Assert.Equal("deep-research", skill.Agent);
        Assert.Equal(["query"], skill.Arguments);
        Assert.True(skill.UserInvocable);
    }
}
