namespace Agency.Harness.Permissions;

/// <summary>The host's answer to a single <see cref="Agency.Harness.PermissionRequestedEvent"/>.</summary>
/// <param name="RequestId">The <see cref="Agency.Harness.PermissionRequestedEvent.RequestId"/> this response corresponds to.</param>
/// <param name="Kind">Whether to allow or deny, and whether the answer applies once or always.</param>
/// <param name="Message">Optional user-supplied reason on a deny; fed back to the LLM.</param>
public sealed record PermissionResponse(Guid RequestId, PermissionResponseKind Kind, string? Message = null);

/// <summary>How the user answered a permission request.</summary>
public enum PermissionResponseKind
{
    /// <summary>Allow this specific invocation; do not persist a rule.</summary>
    AllowOnce,

    /// <summary>Allow this invocation and persist an allow rule so future matching calls are not asked.</summary>
    AllowAlways,

    /// <summary>Deny this specific invocation; do not persist a rule.</summary>
    DenyOnce,

    /// <summary>Deny this invocation and persist a deny rule so future matching calls are blocked automatically.</summary>
    DenyAlways,
}
