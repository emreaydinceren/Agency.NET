using System.Text.Json;

namespace Agency.Harness.Permissions;

/// <summary>Evaluates tool calls against configured allow/deny rules and session grants.</summary>
public interface IPermissionEvaluator
{
    /// <summary>Pure decision — never blocks, never renders, never talks to the user.</summary>
    /// <param name="toolName">The name of the tool being invoked.</param>
    /// <param name="input">The (post-rewrite) tool input to evaluate.</param>
    /// <returns>A <see cref="PermissionDecision"/> indicating whether to allow, deny, or ask the user.</returns>
    PermissionDecision Evaluate(string toolName, JsonElement input);

    /// <summary>Records an "always" answer: adds a session grant and appends to the local rules file.</summary>
    /// <param name="proposedRule">The rule string to persist, e.g. <c>ExecutePowershell(git status)</c>.</param>
    /// <param name="deny"><see langword="true"/> to record a deny grant; <see langword="false"/> to record an allow grant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct);
}
