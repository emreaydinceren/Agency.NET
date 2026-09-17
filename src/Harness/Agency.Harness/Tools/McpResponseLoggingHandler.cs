using System.Net;

namespace Agency.Harness.Tools;

/// <summary>
/// Delegating handler that records the status code of the first HTTP response it observes.
/// </summary>
/// <remarks>
/// The MCP SDK's <c>AutoDetect</c> transport mode tries Streamable HTTP (<c>POST /mcp</c>) first and,
/// on any non-success status, silently falls back to SSE (<c>GET /mcp</c>). If a server requires a
/// bearer token and none is supplied, the real failure is a <c>401</c> on the first attempt, but the
/// SSE fallback then hits a POST-only route and fails with <c>404</c> - and only that second failure
/// reaches the operator. Installing this handler on the transport's <see cref="HttpClient"/> captures
/// the first attempt's status so it can be surfaced alongside the exception message.
/// </remarks>
internal sealed class McpResponseLoggingHandler : DelegatingHandler
{
    private readonly Lock _lock = new();
    private HttpStatusCode? _firstStatus;

    /// <summary>Gets the status code of the first response this handler observed, or <see langword="null"/> if none yet.</summary>
    internal HttpStatusCode? FirstStatus
    {
        get
        {
            lock (_lock)
            {
                return _firstStatus;
            }
        }
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            _firstStatus ??= response.StatusCode;
        }

        return response;
    }
}
