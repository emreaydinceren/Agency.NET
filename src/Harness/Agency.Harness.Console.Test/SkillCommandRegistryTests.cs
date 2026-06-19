using Agency.Harness.Console.Commands;
using Agency.Harness.Skills;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for skill <c>/</c>-command registration via
/// <see cref="CommandRegistry.RegisterSkillCommands"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CommandRegistry"/> is a static class with a shared command list. Each test
/// calls <see cref="CommandRegistry.RegisterSkillCommands"/> with a fresh catalog so the
/// commands it adds are isolated by their unique names. Assertions use name-based lookups
/// rather than index-based ones to remain insensitive to static-constructor ordering and
/// other tests' side effects.
/// </para>
/// <para>
/// The tests for <see cref="Skill.ArgumentHint"/> parsing live alongside the other
/// <see cref="SkillParser"/> tests in <c>Agency.Harness.Test</c>; this file covers
/// only the Console-layer registration and argument-hint surfacing.
/// </para>
/// </remarks>
public sealed class SkillCommandRegistryTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Skill MakeSkill(
        string name,
        bool userInvocable = true,
        bool disableModelInvocation = false,
        string? argumentHint = null) =>
        new()
        {
            Name = name,
            Description = $"Description for {name}",
            Body = $"Body for {name} with $ARGUMENTS",
            SkillDir = $"/skills/{name}",
            UserInvocable = userInvocable,
            DisableModelInvocation = disableModelInvocation,
            ArgumentHint = argumentHint,
        };

    private static SkillCatalog BuildCatalog(params Skill[] skills) => new(skills);

    /// <summary>
    /// Finds a registered command whose <c>CommandText</c> equals <paramref name="commandText"/>
    /// within <see cref="CommandRegistry.Commands"/>. Returns <see langword="null"/> when absent.
    /// </summary>
    private static Command? FindCommand(string commandText) =>
        CommandRegistry.Commands.FirstOrDefault(c =>
            string.Equals(c.CommandText, commandText, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------------------
    // Registration: user-invocable = true
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A skill with <see cref="Skill.UserInvocable"/> set to <see langword="true"/> (the default)
    /// must be registered as a <c>/name</c> command.
    /// </summary>
    [Fact]
    public void RegisterSkillCommands_UserInvocableSkill_IsRegisteredAsSlashCommand()
    {
        SkillCatalog catalog = BuildCatalog(MakeSkill("my-skill"));

        CommandRegistry.RegisterSkillCommands(catalog);

        Command? cmd = FindCommand("/my-skill");
        Assert.NotNull(cmd);
    }

    // ---------------------------------------------------------------------------
    // Registration: user-invocable = false
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A skill with <see cref="Skill.UserInvocable"/> set to <see langword="false"/> must NOT
    /// be registered as a slash command, regardless of <see cref="Skill.DisableModelInvocation"/>.
    /// </summary>
    [Fact]
    public void RegisterSkillCommands_NotUserInvocableSkill_IsNotRegistered()
    {
        SkillCatalog catalog = BuildCatalog(MakeSkill("hidden-skill", userInvocable: false));

        CommandRegistry.RegisterSkillCommands(catalog);

        Command? cmd = FindCommand("/hidden-skill");
        Assert.Null(cmd);
    }

    // ---------------------------------------------------------------------------
    // Registration: disable-model-invocation does NOT suppress user-invocable
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A skill with <c>disable-model-invocation: true</c> but <c>user-invocable: true</c>
    /// (the default) MUST still be registered as a slash command — it is user-only, not hidden.
    /// </summary>
    [Fact]
    public void RegisterSkillCommands_DisabledForModelButUserInvocable_IsRegistered()
    {
        SkillCatalog catalog = BuildCatalog(
            MakeSkill("model-hidden-skill", userInvocable: true, disableModelInvocation: true));

        CommandRegistry.RegisterSkillCommands(catalog);

        Command? cmd = FindCommand("/model-hidden-skill");
        Assert.NotNull(cmd);
    }

    // ---------------------------------------------------------------------------
    // ArgumentHint surfacing
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The <see cref="Command.ArgumentHint"/> of a registered skill command must equal
    /// the <see cref="Skill.ArgumentHint"/> declared in the skill's frontmatter.
    /// </summary>
    [Fact]
    public void RegisterSkillCommands_SkillWithArgumentHint_HintSurfacedOnCommand()
    {
        SkillCatalog catalog = BuildCatalog(MakeSkill("hinted-skill", argumentHint: "<query>"));

        CommandRegistry.RegisterSkillCommands(catalog);

        Command? cmd = FindCommand("/hinted-skill");
        Assert.NotNull(cmd);
        Assert.Equal("<query>", cmd.ArgumentHint);
    }

    /// <summary>
    /// When a skill has no <c>argument-hint</c> frontmatter field, the registered command's
    /// <see cref="Command.ArgumentHint"/> is <see langword="null"/>.
    /// </summary>
    [Fact]
    public void RegisterSkillCommands_SkillWithoutArgumentHint_CommandHintIsNull()
    {
        SkillCatalog catalog = BuildCatalog(MakeSkill("no-hint-skill"));

        CommandRegistry.RegisterSkillCommands(catalog);

        Command? cmd = FindCommand("/no-hint-skill");
        Assert.NotNull(cmd);
        Assert.Null(cmd.ArgumentHint);
    }

    // ---------------------------------------------------------------------------
    // Command description
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The <see cref="Command.Description"/> of a registered skill command must match
    /// the <see cref="Skill.Description"/> from the catalog.
    /// </summary>
    [Fact]
    public void RegisterSkillCommands_SkillDescription_SurfacedAsCommandDescription()
    {
        SkillCatalog catalog = BuildCatalog(MakeSkill("desc-skill"));

        CommandRegistry.RegisterSkillCommands(catalog);

        Command? cmd = FindCommand("/desc-skill");
        Assert.NotNull(cmd);
        Assert.Equal("Description for desc-skill", cmd.Description);
    }

}
