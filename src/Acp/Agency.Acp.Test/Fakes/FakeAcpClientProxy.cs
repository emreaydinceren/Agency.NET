using Agency.Acp.Turns;
using dotacp.protocol;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// A test double for <see cref="IAcpClientProxy"/> that records every notification/request sent to
/// it and answers <see cref="RequestPermissionAsync"/> from a caller-supplied queue.
/// </summary>
internal sealed class FakeAcpClientProxy : IAcpClientProxy
{
    private readonly Queue<RequestPermissionResponse> _permissionResponses = new();

    /// <summary>Gets every <see cref="SessionUpdate"/> sent via <see cref="SendUpdateAsync"/>, in order.</summary>
    public List<SessionUpdate> Updates { get; } = [];

    /// <summary>Gets every <see cref="RequestPermissionRequest"/> sent via <see cref="RequestPermissionAsync"/>, in order.</summary>
    public List<RequestPermissionRequest> PermissionRequests { get; } = [];

    /// <summary>Invoked synchronously at the end of every <see cref="SendUpdateAsync"/> call, after recording the update.</summary>
    public Action? OnUpdateSent { get; set; }

    /// <summary>Enqueues the outcome returned by the next <see cref="RequestPermissionAsync"/> call.</summary>
    public void EnqueuePermissionResponse(RequestPermissionOutcome outcome) =>
        this._permissionResponses.Enqueue(new RequestPermissionResponse { Outcome = outcome });

    /// <inheritdoc/>
    public Task SendUpdateAsync(SessionNotification notification, CancellationToken ct)
    {
        this.Updates.Add(notification.Update);
        this.OnUpdateSent?.Invoke();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct)
    {
        this.PermissionRequests.Add(request);
        if (this._permissionResponses.Count == 0)
        {
            throw new InvalidOperationException("FakeAcpClientProxy has no more queued permission responses.");
        }

        return Task.FromResult(this._permissionResponses.Dequeue());
    }
}
