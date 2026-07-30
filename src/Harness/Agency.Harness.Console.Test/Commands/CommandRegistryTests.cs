using Agency.Harness.Console.Commands;

namespace Agency.Harness.Console.Test.Commands;

/// <summary>
/// Unit tests for the project-command surface registered by <see cref="CommandRegistry"/>'s
/// static constructor: the Spec §6.4 rename from <c>/projects-*</c> to <c>/project-*</c>, the
/// three new commands (<c>/project-create</c>, <c>/project-delete</c>, <c>/project-show</c>),
/// and the dispatch-prefix invariant (Spec §6.4.2) that any registration surface must respect.
/// </summary>
/// <remarks>
/// <see cref="CommandRegistry"/> is a static class with a shared, process-wide command list
/// populated once by its static constructor. These tests only read <see cref="CommandRegistry.Commands"/>
/// — they never mutate it — so they are safe to run alongside other tests that register
/// additional (e.g. skill) commands.
/// </remarks>
public sealed class CommandRegistryTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Command? FindCommand(string commandText) =>
        CommandRegistry.Commands.FirstOrDefault(c =>
            string.Equals(c.CommandText, commandText, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------------------
    // 6.4.1 — clean-break rename: singular naming, no aliases
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The registry must expose exactly the six V1 project commands under their singular
    /// spellings (Spec §6.4's command table), and must NOT retain the old plural spellings
    /// as hidden aliases (Spec §6.4.1 — "clean break, no aliases").
    /// </summary>
    [Fact]
    public void CommandRegistry_ProjectCommands_UseSingularNaming()
    {
        List<string> expected =
        [
            "/project-create",
            "/project-delete",
            "/project-list",
            "/project-load",
            "/project-show",
            "/project-unload",
        ];

        // Every project command — old or new — shares the "/project" stem, so filtering on
        // that prefix captures both the expected six and the (should-be-absent) old plural
        // spellings in one pass: a mismatch here means either a missing new command or a
        // surviving old one.
        List<string> actual = CommandRegistry.Commands
            .Select(c => c.CommandText)
            .Where(t => t.StartsWith("/project", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------------------
    // 6.4.2 — dispatch hazard: prefix invariant
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="CommandManager.ExecuteCommandAsync"/> dispatches via
    /// <c>this._commands.FirstOrDefault(c =&gt; commandText.StartsWith(c.CommandText, StringComparison.OrdinalIgnoreCase))</c>
    /// (verified directly in <c>CommandManager.cs</c>): it walks the registered list in order and
    /// returns the first command whose <c>CommandText</c> is a prefix of the typed input. If a
    /// shorter <c>CommandText</c> is a proper prefix of a longer one AND the shorter is registered
    /// first, any input meant for the longer command is shadowed by the shorter one instead.
    /// </summary>
    /// <remarks>
    /// Scoped to the <c>"/project"</c>-stem commands this deliverable renames/adds (Spec §6.4,
    /// §6.4.2), rather than every registered command. Two reasons: (1) that is the surface this
    /// deliverable actually changes and the invariant it must not break; (2) Spec's error table
    /// (E24) explicitly carves out the cross-cutting case — "a user-invocable skill registers a
    /// <c>/project-…</c> command" — as pre-existing, accepted behaviour ("built-ins are registered
    /// in the static constructor, skills later ⇒ built-ins win... the prefix invariant test (6.4.2)
    /// is understood to cover built-ins only"). Widening this test to the full, unfiltered
    /// <see cref="CommandRegistry.Commands"/> list would also make it sensitive to whatever other
    /// test classes have dynamically registered skill commands into the same shared static list
    /// earlier in the run (e.g. <c>SkillCommandRegistryTests</c>), which is exactly the scenario
    /// E24 says this test is not meant to police.
    /// </remarks>
    [Fact]
    public void CommandRegistry_NoCommandTextIsAProperPrefixOfAnother()
    {
        List<Command> projectCommands = CommandRegistry.Commands
            .Where(c => c.CommandText.StartsWith("/project", StringComparison.OrdinalIgnoreCase))
            .ToList();

        for (int i = 0; i < projectCommands.Count; i++)
        {
            for (int j = 0; j < projectCommands.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                string shorterCandidate = projectCommands[i].CommandText;
                string longerCandidate = projectCommands[j].CommandText;

                bool isProperPrefix =
                    shorterCandidate.Length < longerCandidate.Length &&
                    longerCandidate.StartsWith(shorterCandidate, StringComparison.OrdinalIgnoreCase);

                if (!isProperPrefix)
                {
                    continue;
                }

                // shorterCandidate (index i) is a proper prefix of longerCandidate (index j).
                // Dispatch must reach the longer command first, so it must be registered earlier.
                Assert.True(
                    j < i,
                    $"'{shorterCandidate}' (index {i}) is a proper prefix of '{longerCandidate}' " +
                    $"(index {j}), but the longer command is not registered first. Input meant for " +
                    $"'{longerCandidate}' would be shadowed by '{shorterCandidate}'.");
            }
        }
    }

    // ---------------------------------------------------------------------------
    // 6.4 — argument-hint advertisement
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The five project commands that take a name argument must advertise the
    /// <c>"&lt;name&gt;"</c> argument hint used throughout the picker/help surface; <c>/project-list</c>
    /// takes no argument and must advertise none (Spec §6.4's command table).
    /// </summary>
    [Fact]
    public void CommandRegistry_ProjectCommands_HaveArgumentHints()
    {
        string[] argumentTakingCommands =
        [
            "/project-create",
            "/project-delete",
            "/project-load",
            "/project-unload",
            "/project-show",
        ];

        foreach (string commandText in argumentTakingCommands)
        {
            Command? command = FindCommand(commandText);
            Assert.NotNull(command);
            Assert.Equal("<name>", command.ArgumentHint);
        }

        Command? listCommand = FindCommand("/project-list");
        Assert.NotNull(listCommand);
        Assert.True(string.IsNullOrEmpty(listCommand.ArgumentHint));
    }
}
