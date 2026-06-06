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

    /// <summary>
    /// Flag the tool call for user confirmation. Deny beats Ask; allow rules cannot clear it.
    /// When aggregating, Ask outranks Rewrite and Allow (Deny &gt; Ask &gt; Rewrite &gt; Allow).
    /// The first non-null <paramref name="Reason"/> among multiple Ask results is kept.
    /// </summary>
    public sealed record Ask(string? Reason) : PreToolUseDecision;

    /// <summary>Shorthand singleton for <see cref="Allow"/>.</summary>
    public static PreToolUseDecision Allowed { get; } = new Allow();
}
