using System.Collections.Concurrent;
using Agency.Acp.Errors;
using Agency.Acp.Transport;
using dotacp.protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Turns;

/// <summary>
/// Production <see cref="IAcpClientProxy"/>: sends <c>session/update</c> notifications and
/// <c>session/request_permission</c> requests over the process's single <see cref="StdioTransport"/>,
/// correlating each request's JSON-RPC response by id.
/// </summary>
/// <remarks>
/// A response to one of this proxy's own outgoing requests looks, on the wire, exactly like an
/// incoming line with an <c>id</c> and no <c>method</c>. <see cref="TryHandleResponse"/> must be
/// offered every incoming line before <see cref="Dispatch.MethodDispatcher.DispatchAsync"/> — see
/// <see cref="Program"/> — so such a line is completed here instead of being misreported as a
/// malformed request.
/// </remarks>
internal sealed class AcpClientProxy : IAcpClientProxy
{
    private readonly StdioTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _pending = new();
    private long _nextId;

    /// <param name="transport">The process's single stdio transport.</param>
    public AcpClientProxy(StdioTransport transport)
    {
        this._transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <inheritdoc/>
    public async Task SendUpdateAsync(SessionNotification notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var envelope = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = ClientMethods.SessionUpdate,
            ["params"] = JObject.FromObject(notification),
        };
        await this._transport.SendAsync(envelope.ToString(Formatting.None), ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        long id = Interlocked.Increment(ref this._nextId);
        var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        this._pending[id] = completion;

        using CancellationTokenRegistration registration = ct.Register(() => completion.TrySetCanceled(ct));

        try
        {
            var envelope = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = ClientMethods.SessionRequestPermission,
                ["params"] = JObject.FromObject(request),
            };
            await this._transport.SendAsync(envelope.ToString(Formatting.None), ct).ConfigureAwait(false);

            JObject result = await completion.Task.ConfigureAwait(false);
            return result.ToObject<RequestPermissionResponse>()
                ?? throw new AcpJsonRpcException(ErrorCode.InternalError, "Client returned an empty session/request_permission response.");
        }
        finally
        {
            this._pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Completes the pending <see cref="RequestPermissionAsync"/> call matching <paramref name="envelope"/>'s
    /// <c>id</c>, if any. Returns <see langword="false"/> for any line that is not a response to one
    /// of this proxy's own outgoing requests — including every ordinary incoming request or
    /// notification from the client, which the caller must then hand to
    /// <see cref="Dispatch.MethodDispatcher"/> as usual.
    /// </summary>
    public bool TryHandleResponse(JObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.ContainsKey("method") || !envelope.TryGetValue("id", out JToken? idToken))
        {
            return false;
        }

        if (idToken.Type != JTokenType.Integer || !this._pending.TryGetValue(idToken.Value<long>(), out TaskCompletionSource<JObject>? completion))
        {
            return false;
        }

        if (envelope["error"] is JObject errorObj)
        {
            string message = errorObj.Value<string>("message") ?? "session/request_permission failed.";
            completion.TrySetException(new AcpJsonRpcException(ErrorCode.InternalError, message));
        }
        else
        {
            completion.TrySetResult(envelope["result"] as JObject ?? new JObject());
        }

        return true;
    }
}
