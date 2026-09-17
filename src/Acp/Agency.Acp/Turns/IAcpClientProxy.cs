using dotacp.protocol;

namespace Agency.Acp.Turns;

/// <summary>
/// The client-facing half of the ACP connection that <see cref="TurnDriver"/> drives a turn
/// through: outbound <c>session/update</c> notifications and the <c>session/request_permission</c>
/// round-trip (spec §6.1, §6.4). Kept as a seam so <see cref="TurnDriver"/> can be driven, in tests,
/// against a real <c>Agency.Harness.ChatSession</c>/<c>Agency.Harness.Agent</c> pair without a live
/// stdio connection.
/// </summary>
internal interface IAcpClientProxy
{
    /// <summary>Sends a <c>session/update</c> notification. Fire-and-forget from the wire's perspective.</summary>
    Task SendUpdateAsync(SessionNotification notification, CancellationToken ct);

    /// <summary>
    /// Sends a <c>session/request_permission</c> request and returns the client's answer. Per spec
    /// §6.4/§6.6, this is unreachable in v1 production traffic (the wired-up
    /// <see cref="Agency.Acp.Permissions.PersonaPermissionEvaluator"/> never asks) but must be fully
    /// implemented and tested regardless.
    /// </summary>
    Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct);
}
