using System.Text.Json;
using Agency.Harness.Permissions;

namespace Agency.Acp.Permissions;

/// <summary>
/// The v1 permission evaluator for Agency.Acp sessions (spec §6.6): allows only a configured set
/// of tool names and denies everything else. Deliberately never returns
/// <see cref="PermissionDecision.Ask"/> — a Persona that parks waiting for a human it cannot reach
/// is a hang, not a prompt (spec §6.6). This means the park/resume loop built in
/// <see cref="Agency.Acp.Turns.TurnDriver"/> (spec §6.4) is unreachable through this evaluator in
/// production; it is fully implemented and tested regardless, driven in tests by a test-only
/// evaluator that does return <see cref="PermissionDecision.Ask"/>
/// (<c>Agency.Acp.Test.Fakes.StubPermissionEvaluator</c>). That separation is deliberate, not an
/// oversight: this type is the only <see cref="IPermissionEvaluator"/> Agency.Acp wires up outside
/// of tests.
/// </summary>
/// <remarks>
/// Deliberately not a reuse of <c>Agency.Harness.Permissions.PermissionEvaluator</c> (the Console's
/// evaluator): that type persists "Allow Always" grants to
/// <c>%LocalAppData%\Agency\permissions.local.json</c>, which would leak grants between ACP
/// sessions and processes. <see cref="RecordAlwaysAsync"/> here is a no-op for the same reason — no
/// grant file is ever written (spec §6.6 table, "AllowAlways — never offered").
/// </remarks>
internal sealed class PersonaPermissionEvaluator : IPermissionEvaluator
{
    private HashSet<string> _grantedTools;

    /// <param name="grantedTools">The tool names this evaluator allows; everything else is denied.</param>
    public PersonaPermissionEvaluator(IEnumerable<string> grantedTools)
    {
        ArgumentNullException.ThrowIfNull(grantedTools);
        this._grantedTools = new HashSet<string>(grantedTools, StringComparer.Ordinal);
    }

    /// <summary>
    /// Replaces the granted tool set. Registered as a scoped service in the composition root
    /// (<c>Program.BuildHost</c>), this evaluator is constructed with an empty set — before that
    /// session's <c>McpClientPool</c> exists — and <see cref="Sessions.SessionFactory.CreateAsync"/>
    /// calls this once the pool has connected and the session's real tool names are known. The
    /// granted set is explicit and derived from that list, never implicit (spec §6.6).
    /// </summary>
    /// <param name="grantedTools">The tool names this session's <c>McpClientPool</c> actually exposes.</param>
    internal void SetGrantedTools(IEnumerable<string> grantedTools)
    {
        ArgumentNullException.ThrowIfNull(grantedTools);
        this._grantedTools = new HashSet<string>(grantedTools, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    /// <remarks>Never returns <see cref="PermissionDecision.Ask"/> (spec §6.6).</remarks>
    public PermissionDecision Evaluate(string toolName, JsonElement input) =>
        this._grantedTools.Contains(toolName)
            ? PermissionDecision.Allowed
            : new PermissionDecision.Deny($"Tool '{toolName}' is not in this session's granted tool set.");

    /// <inheritdoc/>
    /// <remarks>No-op: no grant file is ever written for an ACP session (spec §6.6).</remarks>
    public Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct) => Task.CompletedTask;
}
