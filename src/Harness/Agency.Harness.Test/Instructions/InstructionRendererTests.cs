using Agency.Harness.Instructions;

namespace Agency.Harness.Test.Instructions;

/// <summary>Tests for <see cref="InstructionRenderer"/>.</summary>
public sealed class InstructionRendererTests
{
    /// <summary>Verifies that empty contexts render to empty string.</summary>
    [Fact]
    public void Build_EmptyContext_ReturnsEmptyString()
    {
        // Act
        var result = InstructionRenderer.Build(InstructionContext.Empty);

        // Assert
        Assert.Empty(result);
    }

    /// <summary>Verifies that instruction content is preserved in full in the rendered output.</summary>
    [Fact]
    public void Build_WithRepoRootSource_PreservesFullContent()
    {
        // Arrange
        var testContent = """
            # Memory MCP Server — Agent Instructions

            ## Persistent Memory Through Memorize

            Save durable facts about this user with `memorize` as you learn them, and fetch them with `recall`. When the user shares a clear reference, pattern, technique, or durable fact that would be regrettable to lose or that recurs across sessions, offer to memorize it.

            The agent can explicitly memorize facts the user shares using the Memorize tool. When a user shares a clear preference, pattern, technique, or durable fact that would be regrettable to lose or that recurs across sessions, proactively offer to save it:

            > "That sounds like something worth remembering. Would you like me to memorize that?"

            ## When to Memorize

            Use `memorize` for facts that meet any of these criteria:

            - **Recurring patterns** — workflows, preferences, or techniques the user mentions across multiple sessions
            - **Personal knowledge** — domain expertise, coding conventions, or project-specific standards the user has explained
            - **Hard-won insights** — debugging techniques, performance tips, or solutions to problems the user has discovered
            - **Explicit requests** — when the user directly asks you to remember something
            - **Critical context** — project goals, architectural decisions, or constraints that shape all interactions

            ## When NOT to Memorize

            Avoid memorizing:

            - Ephemeral task details (the current problem being debugged, a one-off question)
            - Information already in the codebase or documentation
            - Session-specific state (what was just discussed, current file being edited)
            - Duplicates of what's already memorized

            ## Usage Pattern

            1. User shares a fact or technique
            2. Recognize it as memorization-worthy
            3. Offer: "That sounds like something worth remembering. Would you like me to memorize that?"
            4. If yes: call `memorize` with a clear domain and fact
            5. On future sessions: `recall` to surface relevant facts for the current task
            """;

        var source = new InstructionSource("test-location", testContent, InstructionSourceKind.RepoRoot);
        var ctx = new InstructionContext(new[] { source });

        // Act
        var result = InstructionRenderer.Build(ctx);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains(testContent, result);
        Assert.Contains("<project-instructions>", result);
        Assert.Contains("</project-instructions>", result);
        Assert.Contains("(project instructions, checked into the repo)", result);
    }

    /// <summary>Verifies that multiple sources are rendered with correct provenance labels.</summary>
    [Fact]
    public void Build_WithMultipleSources_RendersProvenanceCorrectly()
    {
        // Arrange
        var repoRootSource = new InstructionSource("repo-root-file", "Repo content", InstructionSourceKind.RepoRoot);
        var ancestorSource = new InstructionSource("ancestor-file", "Ancestor content", InstructionSourceKind.Ancestor);
        var mcpSource = new InstructionSource("mcp-resource", "MCP content", InstructionSourceKind.Mcp, "test-server");

        var ctx = new InstructionContext(new[] { repoRootSource, ancestorSource, mcpSource });

        // Act
        var result = InstructionRenderer.Build(ctx);

        // Assert
        Assert.Contains("(project instructions, checked into the repo)", result);
        Assert.Contains("(ancestor directory instructions)", result);
        Assert.Contains("(via MCP server 'test-server')", result);
        Assert.Contains("Repo content", result);
        Assert.Contains("Ancestor content", result);
        Assert.Contains("MCP content", result);
    }
}
