using System.Text.Json;
using Agency.Harness.Permissions;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// A test-only <see cref="IPermissionEvaluator"/> that DOES return <see cref="PermissionDecision.Ask"/>
/// (configurable per tool name; missing entries default to <see cref="PermissionDecision.Allowed"/>).
/// </summary>
/// <remarks>
/// This is deliberately separate from <c>Agency.Acp.Permissions.PersonaPermissionEvaluator</c>, the
/// only evaluator Agency.Acp wires up in production, which never asks (spec §6.6). This type exists
/// solely to drive the park/resume loop (spec §6.4) in tests, since the production evaluator never
/// exercises it.
/// </remarks>
internal sealed class StubPermissionEvaluator : IPermissionEvaluator
{
    /// <summary>Maps tool name → decision returned by <see cref="Evaluate"/>.</summary>
    public Dictionary<string, PermissionDecision> Decisions { get; } = new(StringComparer.Ordinal);

    /// <summary>Records each (toolName, input) pair passed to <see cref="Evaluate"/>, in call order.</summary>
    public List<(string ToolName, JsonElement Input)> EvaluateCalls { get; } = [];

    /// <summary>Records each (proposedRule, deny) pair passed to <see cref="RecordAlwaysAsync"/>.</summary>
    public List<(string ProposedRule, bool Deny)> RecordAlwaysCalls { get; } = [];

    /// <inheritdoc/>
    public PermissionDecision Evaluate(string toolName, JsonElement input)
    {
        this.EvaluateCalls.Add((toolName, input));
        return this.Decisions.TryGetValue(toolName, out PermissionDecision? decision)
            ? decision
            : PermissionDecision.Allowed;
    }

    /// <inheritdoc/>
    public Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct)
    {
        this.RecordAlwaysCalls.Add((proposedRule, deny));
        return Task.CompletedTask;
    }
}
