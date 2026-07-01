using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using System.Text.Json;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="UserIdPlaceholderHook"/>, the OnPreToolUse hook that substitutes the
/// <c>{userId}</c> placeholder in tool-call arguments with the session's resolved user id.
/// </summary>
public sealed class UserIdPlaceholderHookTests
{
    private static Context ContextWithUserId(string? userId) =>
        new()
        {
            Query = new QueryContext { Prompt = "p" },
            User = new UserSpecificContext { Id = userId }
        };

    private static async Task<PreToolUseDecision> InvokeAsync(string toolName, string inputJson, string? userId)
    {
        using var doc = JsonDocument.Parse(inputJson);
        var ctx = new PreToolUseHookContext(toolName, doc.RootElement, ContextWithUserId(userId));
        return await UserIdPlaceholderHook.Hooks.OnPreToolUse!(ctx, CancellationToken.None);
    }

    /// <summary>
    /// A <c>{userId}</c> placeholder in the tool arguments is rewritten with the session's resolved user ID.
    /// </summary>
    [Fact]
    public async Task PlaceholderPresent_RewritesWithResolvedUserId()
    {
        PreToolUseDecision decision = await InvokeAsync(
            "list_global_keys",
            """{"memoryScope":{"UserId":"{userId}"}}""",
            "abc-123");

        var rewrite = Assert.IsType<PreToolUseDecision.Rewrite>(decision);
        string rewritten = rewrite.NewInput.GetRawText();
        Assert.DoesNotContain("{userId}", rewritten, StringComparison.Ordinal);
        Assert.Equal(
            "abc-123",
            rewrite.NewInput.GetProperty("memoryScope").GetProperty("UserId").GetString());
    }

    /// <summary>
    /// Tool calls whose arguments contain no <c>{userId}</c> placeholder are allowed unmodified.
    /// </summary>
    [Fact]
    public async Task NoPlaceholder_Allows()
    {
        PreToolUseDecision decision = await InvokeAsync(
            "read_file",
            """{"path":"c:/tmp/x.txt"}""",
            "abc-123");

        Assert.IsType<PreToolUseDecision.Allow>(decision);
    }

    /// <summary>
    /// When no user ID has been resolved, the placeholder is left intact and the call proceeds
    /// rather than being blocked.
    /// </summary>
    [Fact]
    public async Task EmptyUserId_Allows_WithoutSubstituting()
    {
        // With no resolved id there is nothing to substitute; the placeholder is left intact and the
        // call proceeds (the memory server will then return its own friendly "UserId is required" error).
        PreToolUseDecision decision = await InvokeAsync(
            "list_global_keys",
            """{"memoryScope":{"UserId":"{userId}"}}""",
            userId: null);

        Assert.IsType<PreToolUseDecision.Allow>(decision);
    }
}
