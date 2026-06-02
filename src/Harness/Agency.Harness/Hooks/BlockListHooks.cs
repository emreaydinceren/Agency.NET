namespace Agency.Harness.Hooks;

/// <summary>Pre-built hooks that block dangerous shell commands.</summary>
public static class BlockListHooks
{
    private static readonly string[] _dangerous =
        ["rm -rf", "drop table", "format c:", "del /f /s"];

    private static readonly HashSet<string> _shellToolNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Bash",
            "ExecutePowershell",
            "ExecutePowershellTool",
        };

    /// <summary>
    /// Denies tool calls whose <c>command</c> argument matches a known-dangerous pattern.
    /// Only applies to shell-execution tools (Bash, ExecutePowershell, ExecutePowershellTool).
    /// </summary>
    public static AgentHooks Dangerous { get; } = new()
    {
        OnPreToolUse = (ctx, _) =>
        {
            if (!_shellToolNames.Contains(ctx.ToolName))
            {
                return Task.FromResult(PreToolUseDecision.Allowed);
            }

            string? command = ctx.Input.TryGetProperty("command", out System.Text.Json.JsonElement prop)
                ? prop.GetString()
                : null;

            if (command is not null)
            {
                foreach (string pattern in _dangerous)
                {
                    if (command.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult<PreToolUseDecision>(
                            new PreToolUseDecision.Deny($"Blocked: '{pattern}' is a dangerous pattern."));
                    }
                }
            }

            return Task.FromResult(PreToolUseDecision.Allowed);
        },
    };
}