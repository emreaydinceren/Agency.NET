using System.Text.Json;

namespace Agency.Harness.Hooks;

/// <summary>The decision returned by an <c>OnPreToolUse</c> hook delegate.</summary>
public abstract record PreToolUseDecision
{
    internal PreToolUseDecision() { }

    /// <summary>Allow the tool call to proceed with the original input.</summary>
    public sealed record Allow : PreToolUseDecision;

    /// <summary>Block the tool call. <paramref name="Reason"/> is returned to the agent as a tool error.</summary>
    public sealed record Deny(string Reason) : PreToolUseDecision;

    /// <summary>Rewrite the tool's input before invocation.</summary>
    public sealed record Rewrite(JsonElement NewInput) : PreToolUseDecision;

    /// <summary>Shorthand singleton for <see cref="Allow"/>.</summary>
    public static PreToolUseDecision Allowed { get; } = new Allow();
}
