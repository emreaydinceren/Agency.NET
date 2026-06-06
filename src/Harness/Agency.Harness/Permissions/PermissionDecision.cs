namespace Agency.Harness.Permissions;

/// <summary>The result of evaluating a tool call against the permission rules.</summary>
public abstract record PermissionDecision
{
    /// <summary>The tool call is permitted to proceed.</summary>
    public sealed record Allow : PermissionDecision;

    /// <summary>The tool call is blocked. <paramref name="Reason"/> is returned to the agent as a tool error.</summary>
    public sealed record Deny(string Reason) : PermissionDecision;

    /// <summary>No rule matched; the call must be surfaced to the user.</summary>
    /// <param name="KeyValue">Extracted key field (command/path) for concise display; <see langword="null"/> when none.</param>
    /// <param name="ProposedRule">Rule string that would be persisted if the user answers "always".</param>
    public sealed record Ask(string? KeyValue, string ProposedRule) : PermissionDecision;

    /// <summary>Shorthand singleton for <see cref="Allow"/>.</summary>
    public static PermissionDecision Allowed { get; } = new Allow();

    private PermissionDecision() { }
}
