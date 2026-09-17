using System.Net;
using System.Net.Sockets;

namespace Agency.Harness.Test.Tools;

/// <summary>
/// Unit test reproducing the real-world failure where an MCP server requires a bearer token, returns
/// <c>401</c> to <c>POST /mcp</c> when none is supplied, and the SDK's <c>AutoDetect</c> transport mode
/// misreads that as "this server does not speak Streamable HTTP", falls back to SSE, issues
/// <c>GET /mcp</c>, hits a POST-only route, and surfaces that <c>404</c> instead - hiding the real
/// authentication failure from the operator.
/// </summary>
public sealed class McpAutoDetectDiagnosticsTests
{
    /// <summary>
    /// A server that requires auth and returns <c>401</c> to the first (Streamable HTTP) attempt must
    /// have that first-attempt status surfaced in <see cref="McpClientPool.FailedServers"/>, not just the
    /// <c>404</c> from the SSE fallback's <c>GET</c>.
    /// </summary>
    [Fact]
    public async Task CreateAsync_WhenServerRequiresAuth_RecordsFirstAttemptStatus()
    {
        int port = GetFreeLoopbackPort();
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Task serverTask = RunServerAsync(listener, TestContext.Current.CancellationToken);

        try
        {
            McpClientOptions options = new()
            {
                Servers =
                [
                    new McpServerConfig
                    {
                        Name = "x",
                        Transport = McpTransportKind.Http,
                        Url = $"http://127.0.0.1:{port}/mcp"
                    }
                ]
            };

            McpClientPool pool = await McpClientPool.CreateAsync(options, ct: TestContext.Current.CancellationToken);

            Assert.Contains("x", pool.FailedServers.Keys);
            Assert.Contains("401", pool.FailedServers["x"], StringComparison.Ordinal);

            await pool.DisposeAsync();
        }
        finally
        {
            listener.Stop();
            listener.Close();
        }

        await serverTask;
    }

    /// <summary>
    /// Reserves a free TCP port on the loopback interface for the in-process <see cref="HttpListener"/>.
    /// </summary>
    private static int GetFreeLoopbackPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Answers <c>POST /mcp</c> with <c>401</c> (auth required) and everything else - including the SSE
    /// fallback's <c>GET /mcp</c> - with <c>404</c>, mirroring a real Streamable-HTTP-only MCP server.
    /// </summary>
    private static async Task RunServerAsync(HttpListener listener, CancellationToken ct)
    {
        while (listener.IsListening && !ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            context.Response.StatusCode = string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                ? (int)HttpStatusCode.Unauthorized
                : (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }
    }
}
