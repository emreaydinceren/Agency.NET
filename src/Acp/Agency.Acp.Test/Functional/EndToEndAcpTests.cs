using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using dotacp.client;
using dotacp.protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Agency.Acp.Test.Functional;

/// <summary>
/// End-to-end functional test: a scripted ACP client drives the real <c>agency-acp</c> process over
/// stdio through the sequence in spec Ā§13 (<c>initialize</c> → <c>session/new</c> with a
/// bearer-authenticated MCP server → <c>session/set_config_option</c> → <c>session/prompt</c>), then
/// exercises <c>session/cancel</c> mid-turn.
/// <para>
/// Proves T-25 (spec Ā§15) — "the Agency half" of the joint milestone with Agency.Huddle — at the
/// protocol boundary: O-1 (live streaming), O-2 (authenticated MCP), O-3 (clean cancel, resumable
/// session) and O-5 (no filesystem/shell tool reaches the client). It is deliberately not the
/// milestone itself: two Personas in one Room, live chunk rendering, and a Stop click leaving both
/// resumable is a joint property that also depends on Agency.Huddle's repository.
/// </para>
/// <para>
/// Run with:  <c>dotnet test --filter "Category=Functional" -- RunConfiguration.MaxCpuCount=1</c><br/>
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c><br/>
/// Requires LM Studio (via the caching proxy in <c>src/shared-test-appsettings.json</c>) serving
/// exactly <c>google/gemma-4-e2b</c> — the only model this box's owner has authorised.
/// </para>
/// </summary>
[Trait("Category", "Functional")]
[Trait("Category", "RequiresLlm")]
public sealed class EndToEndAcpTests(EndToEndAcpTests.AcpEndToEndFixture fixture)
    : IClassFixture<EndToEndAcpTests.AcpEndToEndFixture>
{
    /// <summary>
    /// The harness's built-in tools plus the "skill" meta-tool (spec Ā§6.5's five locks / V1GuaranteeTests).
    /// O-5 requires that none of these ever appear as a <c>tool_call</c> the client observes, live.
    /// </summary>
    private static readonly string[] ForbiddenBuiltInToolNames =
    [
        "read_file", "write_file", "execute_powershell", "subagent_tool", "skill",
    ];

    private readonly AcpEndToEndFixture _fixture = fixture;

    // ── (nothing else at class scope: everything test-specific lives in the Fact methods below) ──

    /// <summary>
    /// Standalone control check: the in-process MCP tool server this test stands up requires its
    /// bearer token — a POST to <c>/mcp</c> with no <c>Authorization</c> header is rejected with
    /// <c>401</c>. This is a precondition for O-2's meaning: only once this holds does a *successful*
    /// authenticated connection through the adapter prove anything about <see cref="Agency.Harness.Tools.McpServerConfig.Headers"/>
    /// (D-1) rather than about a server that never checked in the first place.
    /// </summary>
    [Fact]
    public async Task McpToolServer_RequiresBearerToken_Returns401WithoutIt()
    {
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, this._fixture.McpEndpoint)
        {
            Content = new StringContent(string.Empty),
        };

        HttpResponseMessage response = await httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Drives <c>initialize</c> → <c>session/new</c> (bearer-authenticated MCP server) →
    /// <c>session/set_config_option</c> → <c>session/prompt</c> against the real process.
    /// <para>
    /// (a) O-1: asserts an <c>agent_message_chunk</c> arrives strictly before the terminal
    /// <c>session/prompt</c> response, by timestamp.
    /// </para>
    /// <para>
    /// (b) O-2: asserts, deterministically and independent of the model, that the adapter's
    /// <see cref="Agency.Harness.Tools.McpClientPool"/> completed an authenticated <c>tools/list</c>
    /// against this test's bearer-guarded server by the time <c>session/new</c> returns — this is
    /// what exercises <see cref="Agency.Harness.Tools.McpServerConfig.Headers"/> → <c>AdditionalHeaders</c> (D-1). Whether
    /// <c>google/gemma-4-e2b</c> — a very small model — actually elects to *call* the tool is a
    /// separate, model-dependent question (spec Ā§12 E-14: no reliable signal exists for tool support,
    /// and a model may simply ignore tools). It is recorded, not asserted, matching the existing
    /// convention in <c>AgentOpenAIFunctionalTests.Agent_WithTool_InvokesToolAndContinues</c>.
    /// </para>
    /// <para>
    /// (d) O-5: asserts none of the <c>tool_call</c> updates observed during this live turn name a
    /// built-in filesystem/shell tool or the <c>skill</c> meta-tool — the only tool this session's
    /// registry can ever contain is whatever this test's own MCP server advertised.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FullTurn_StreamsChunkBeforeTerminal_AndDiscoversAuthenticatedMcpTool()
    {
        Connection connection = this._fixture.Connection;
        string nonce = Guid.NewGuid().ToString("n")[..8];

        var mcpServer = new McpServerHttp
        {
            Name = "team",
            Url = this._fixture.McpEndpoint,
            Headers = [new HttpHeader { Name = "Authorization", Value = $"Bearer {this._fixture.BearerToken}" }],
        };

        NewSessionResponse newSession = await connection.NewSessionAsync(
            new NewSessionRequest { Cwd = Path.GetTempPath(), McpServers = [mcpServer] },
            TestContext.Current.CancellationToken);

        string sessionId = newSession.SessionId;
        Assert.False(string.IsNullOrEmpty(sessionId));

        // (b) O-2, adapter half — deterministic, no model involved: SessionFactory.CreateAsync
        // (spec Ā§8.1 step 7) connects every mcpServers[] entry and calls tools/list before
        // session/new ever returns. If the bearer header the adapter attached did not match what
        // this test's server requires, McpClientPool.CreateAsync fails soft (session still starts,
        // per spec, with fewer tools) and tools/list would simply never have reached the server.
        Assert.True(
            this._fixture.ListToolsInvoked,
            "The authenticated MCP server never received tools/list — the adapter's Headers -> AdditionalHeaders path did not connect.");

        await connection.SetSessionConfigOptionAsync(
            new SetSessionConfigOptionRequest
            {
                SessionId = sessionId,
                ConfigId = "model",
                Value = (SessionConfigValueId)this._fixture.Model,
            },
            TestContext.Current.CancellationToken);

        SessionCapture capture = this._fixture.GetOrAddCapture(sessionId);

        // (a) O-1 is asserted on a PROSE-ONLY turn, deliberately separate from the tool-calling
        // turn below. O-1 is a property of streaming order — chunks must reach the client before
        // the terminal response — and nothing about it depends on tool support. Asserting it on a
        // tool-invoking prompt couples it to whether this model elects to emit prose alongside a
        // tool call, which spec §12 (E-14) records as unreliable and unsignallable: a turn whose
        // every assistant message is tool-call-only yields no agent_message_chunk at all, failing
        // a sound protocol assertion for an unrelated reason. Observed exactly once in a full-suite
        // run; 5/5 and 2/2 green in isolation and same-assembly reruns.
        PromptResponse proseResponse = await WaitWithTimeoutAsync(
            connection.PromptAsync(
                new PromptRequest
                {
                    SessionId = sessionId,
                    Prompt = [new TextContent { Text = "Reply with one short sentence about the colour of the sky." }],
                },
                CancellationToken.None),
            TimeSpan.FromSeconds(90),
            "session/prompt (O-1 prose turn)",
            this._fixture);

        long proseTerminalTimestamp = Stopwatch.GetTimestamp();
        Assert.Equal(StopReason.EndTurn, proseResponse.StopReason);
        Assert.True(
            capture.HasFirstChunk,
            "No agent_message_chunk was ever received for the prose turn — the adapter did not stream.");
        Assert.True(
            capture.FirstChunkTimestamp < proseTerminalTimestamp,
            "O-1 violated: the terminal session/prompt response arrived before any agent_message_chunk.");

        // A small number of reformulations (per the task's own guidance) to make the tool call more
        // likely on a very small model — never a signal we can rely on (spec E-14).
        string[] proddingPrompts =
        [
            $"Call the get_help tool with topic=\"{nonce}\", then tell me exactly what it returned.",
            $"You have access to an MCP tool named 'get_help' that takes a 'topic' string argument. Use it now with topic=\"{nonce}\" before replying.",
        ];

        PromptResponse response = new() { StopReason = StopReason.Refusal };
        for (int attempt = 0; attempt < proddingPrompts.Length; attempt++)
        {
            Task<PromptResponse> promptTask = connection.PromptAsync(
                new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = proddingPrompts[attempt] }] },
                CancellationToken.None);

            response = await WaitWithTimeoutAsync(promptTask, TimeSpan.FromSeconds(90), $"session/prompt (attempt {attempt + 1})", this._fixture);

            if (this._fixture.CallToolInvoked)
            {
                break;
            }
        }

        Assert.Equal(StopReason.EndTurn, response.StopReason);

        // Model-dependent (spec Ā§12 E-14): recorded for the report, never asserted — a small local
        // model may simply decline every tool, which is not an adapter defect.
        _ = this._fixture.CallToolInvoked;

        // (d) O-5: whatever tools this turn actually invoked, none of them is a built-in fs/shell
        // tool or the skill meta-tool — the session's registry can only ever contain what this
        // test's own MCP server advertised (spec Ā§6.5).
        foreach (string forbidden in ForbiddenBuiltInToolNames)
        {
            Assert.DoesNotContain(forbidden, capture.ToolCallTitles);
        }
    }

    /// <summary>
    /// (c) O-3: <c>session/cancel</c> sent the moment the first chunk of a long, freshly-generated
    /// answer arrives returns <c>stopReason: cancelled</c>, the text streamed so far is retained
    /// client-side, and — because the transcript was left valid (D-4's repair-on-cancel closes any
    /// orphaned <c>tool_use</c>, though this text-only turn calls no tool) — a fresh prompt on the
    /// *same* session completes normally afterwards.
    /// </summary>
    [Fact]
    public async Task Cancel_MidTurn_ReturnsCancelledWithPartialText_AndNextPromptSucceeds()
    {
        Connection connection = this._fixture.Connection;

        NewSessionResponse newSession = await connection.NewSessionAsync(
            new NewSessionRequest { Cwd = Path.GetTempPath() },
            TestContext.Current.CancellationToken);
        string sessionId = newSession.SessionId;

        SessionCapture capture = this._fixture.GetOrAddCapture(sessionId);

        // Fire session/cancel — a notification, per spec Ā§6.4 — the instant the first streamed chunk
        // for this turn is observed, to land the cancel genuinely mid-turn rather than racing a sleep.
        capture.OnFirstChunk = () => _ = connection.CancelAsync(new CancelNotification { SessionId = sessionId }, CancellationToken.None);

        // A fresh nonce keeps this request off the caching proxy (src/shared-test-appsettings.json's
        // TestProxy is a cache), forcing a genuine, freshly-generated, cancellable stream rather than
        // an instantly-replayed cached one.
        string nonce = Guid.NewGuid().ToString("n")[..8];
        Task<PromptResponse> promptTask = connection.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt =
                [
                    new TextContent
                    {
                        Text = $"Write a 200-word paragraph about the history of tea. Do not call any tools. Include the token {nonce} somewhere in your answer.",
                    },
                ],
            },
            CancellationToken.None);

        PromptResponse response = await WaitWithTimeoutAsync(promptTask, TimeSpan.FromSeconds(120), "the cancelled turn", this._fixture);

        Assert.Equal(StopReason.Cancelled, response.StopReason);
        Assert.False(string.IsNullOrEmpty(capture.JoinedText()), "No partial text was retained before the cancel took effect.");

        // The transcript was left valid: a fresh prompt on the SAME session succeeds normally.
        string nonce2 = Guid.NewGuid().ToString("n")[..8];
        Task<PromptResponse> followUpTask = connection.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = [new TextContent { Text = $"Reply with exactly one word: {nonce2}" }],
            },
            CancellationToken.None);

        PromptResponse followUpResponse = await WaitWithTimeoutAsync(followUpTask, TimeSpan.FromSeconds(90), "the follow-up turn after cancel", this._fixture);

        Assert.Equal(StopReason.EndTurn, followUpResponse.StopReason);
    }

    /// <summary>
    /// Task 26 (spec §15 T-17, §13, Appendix B): drives two independent sessions on the shared
    /// process, each carrying a distinctive nonce token in <c>_meta.systemPrompt</c> with a strong,
    /// unambiguous instruction to emit it, then asserts:
    /// <para>
    /// (a) the reply from each session contains ITS OWN nonce — proving the identity reached the
    /// model, not merely the adapter. Identity *delivery* to the prompt boundary is already proven,
    /// deterministically, by <c>ChatSessionIdentityTests.SendAsync_TwoIterationTurn_IdentityPresentOnBothIterations</c>;
    /// this assertion is the model-dependent one (spec §12 E-14: no reliable signal exists for
    /// instruction-following on a very small model) and was run repeatedly during development to
    /// gauge stability before being fixed here — see the task report.
    /// </para>
    /// <para>
    /// (b) spec O-3: neither session's reply contains the OTHER session's nonce — no identity bleed
    /// between two sessions on one process. This assertion does not depend on model
    /// instruction-following: even a model that ignores its own identity entirely would still need
    /// to spontaneously emit the other session's nonce for this to fail, which the nonces are chosen
    /// to make implausible.
    /// </para>
    /// <para>
    /// (c) no <c>tool_call</c> observed on either session names a built-in filesystem/shell tool or
    /// the <c>skill</c> meta-tool (the v1 safety guarantee, spec §6.5) — structural, not
    /// model-dependent: neither session advertises any MCP server, so the registry can only ever
    /// contain the harness's own built-ins, none of which this assertion permits to surface.
    /// </para>
    /// <para>
    /// Nonces are fixed literals, not per-run random values: the shared <c>TestProxy</c> caching
    /// proxy (src/shared-test-appsettings.json) keys its cassette on the literal request text, and a
    /// fixed identity lets CI's offline cache proxy replay a once-recorded response instead of
    /// requiring a live LLM on every run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Identity_ReachesModel_AndDoesNotBleedBetweenSessions()
    {
        Connection connection = this._fixture.Connection;
        const string question = "What is the capital of France? Answer in one short sentence.";

        const string nonceA = "GLIMMERFOX91";
        const string nonceB = "SPARKTOAD64";

        SessionCapture captureA = await NewSessionWithIdentityAndPromptAsync(connection, nonceA, question, this._fixture);
        SessionCapture captureB = await NewSessionWithIdentityAndPromptAsync(connection, nonceB, question, this._fixture);

        string textA = captureA.JoinedText();
        string textB = captureB.JoinedText();

        // (a) model-dependent: each session's reply reflects ITS OWN identity.
        Assert.Contains(nonceA, textA, StringComparison.Ordinal);
        Assert.Contains(nonceB, textB, StringComparison.Ordinal);

        // (b) O-3, structural: no bleed — neither session's reply contains the OTHER session's nonce.
        Assert.DoesNotContain(nonceB, textA, StringComparison.Ordinal);
        Assert.DoesNotContain(nonceA, textB, StringComparison.Ordinal);

        // (c) structural: no built-in fs/shell tool or the skill meta-tool ever appears as a
        // tool_call, on either session — neither advertises any MCP server of its own.
        foreach (string forbidden in ForbiddenBuiltInToolNames)
        {
            Assert.DoesNotContain(forbidden, captureA.ToolCallTitles);
            Assert.DoesNotContain(forbidden, captureB.ToolCallTitles);
        }
    }

    /// <summary>
    /// Opens a fresh session whose <c>_meta.systemPrompt</c> instructs the model to lead its reply
    /// with <paramref name="nonce"/>, sends <paramref name="prompt"/>, and returns the session's
    /// <see cref="SessionCapture"/> once the turn ends normally.
    /// </summary>
    private static async Task<SessionCapture> NewSessionWithIdentityAndPromptAsync(
        Connection connection, string nonce, string prompt, AcpEndToEndFixture fixture)
    {
        string identity =
            $"You are a test agent. You MUST begin every reply with the exact token {nonce} before any other text, with no punctuation or words before it.";

        NewSessionResponse newSession = await connection.NewSessionAsync(
            new NewSessionRequest
            {
                Cwd = Path.GetTempPath(),
                Meta = new Dictionary<string, object> { ["systemPrompt"] = identity },
            },
            TestContext.Current.CancellationToken);

        SessionCapture capture = fixture.GetOrAddCapture(newSession.SessionId);

        PromptResponse response = await WaitWithTimeoutAsync(
            connection.PromptAsync(
                new PromptRequest { SessionId = newSession.SessionId, Prompt = [new TextContent { Text = prompt }] },
                CancellationToken.None),
            TimeSpan.FromSeconds(90),
            $"session/prompt (identity turn, nonce {nonce})",
            fixture);

        Assert.Equal(StopReason.EndTurn, response.StopReason);
        return capture;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Awaits <paramref name="task"/>, failing with a diagnosable <see cref="TimeoutException"/> —
    /// including the captured stdio wire log — rather than hanging, if it does not complete within
    /// <paramref name="timeout"/>. Never cancels the underlying ACP call itself: a client-side give-up
    /// must not be confused with the protocol's own <c>session/cancel</c>, which is what
    /// <see cref="Cancel_MidTurn_ReturnsCancelledWithPartialText_AndNextPromptSucceeds"/> exercises.
    /// </summary>
    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout, string what, AcpEndToEndFixture fixture)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(timeout));
        if (winner != task)
        {
            throw new TimeoutException(
                $"Timed out waiting for {what} after {timeout}. Wire log:\n{string.Join('\n', fixture.WireLog)}");
        }

        return await task;
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared, class-scoped fixture: starts the in-process bearer-authenticated MCP tool server once,
    /// spawns the real <c>Agency.Acp.dll</c> process once, and negotiates <c>initialize</c> once. Each
    /// <see cref="EndToEndAcpTests"/> test method opens its own <c>session/new</c> on the shared
    /// process — sessions are cheap and independent (spec Ā§16 G-1) — so the expensive, physics-bound
    /// cold-model warmup (spec Ā§11.1: 10-60s) is paid at most once per test run, not once per test.
    /// </summary>
    public sealed class AcpEndToEndFixture : IAsyncLifetime
    {
        private readonly ConcurrentQueue<string> _wireLog = new();
        private RecordingAcpClient? _client;
        private Process? _agentProcess;
        private WebApplication? _mcpApp;

        /// <summary>Loads the LM Studio endpoint/model configuration and resolves the built agent exe.</summary>
        public AcpEndToEndFixture()
        {
            // Reads src/shared-test-appsettings.json's TestProxy:* keys directly — no local
            // appsettings.json (see the csproj comment on why one is deliberately not added) and no
            // placeholder expansion needed, since these values are already concrete.
            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddSharedConfiguration("shared-test-appsettings.json", optional: false)
                .Build();

            this.Model = GetRequired(configuration, "TestProxy:Model");
            this.BaseUrl = GetRequired(configuration, "TestProxy:OpenAI:BaseUrl");
            this.ApiKey = GetRequired(configuration, "TestProxy:ApiKey");
            this.AgentDllPath = ResolveAgentDllPath();
        }

        /// <summary>Gets the model id this run is authorised to use — always <c>google/gemma-4-e2b</c>.</summary>
        public string Model { get; }

        /// <summary>Gets the OpenAI-style base URL — the caching proxy at <c>http://llm-host.example:12345/v1</c>.</summary>
        public string BaseUrl { get; }

        /// <summary>Gets the placeholder API key the proxy expects.</summary>
        public string ApiKey { get; }

        /// <summary>Gets the resolved path to the built <c>Agency.Acp.dll</c>.</summary>
        public string AgentDllPath { get; }

        /// <summary>Gets the loopback URL of the in-process MCP tool server's <c>/mcp</c> endpoint.</summary>
        public string McpEndpoint { get; private set; } = string.Empty;

        /// <summary>Gets the bearer token the MCP tool server requires.</summary>
        public string BearerToken { get; private set; } = string.Empty;

        /// <summary>Gets whether the MCP tool server has received an authenticated <c>tools/list</c> call.</summary>
        public bool ListToolsInvoked { get; private set; }

        /// <summary>Gets whether the MCP tool server has received an authenticated <c>tools/call</c> call.</summary>
        public bool CallToolInvoked { get; private set; }

        /// <summary>Gets the live JSON-RPC connection to the spawned <c>agency-acp</c> process.</summary>
        public Connection Connection { get; private set; } = null!;

        /// <summary>Gets the captured stdio wire log (both directions) plus drained stderr, newest last.</summary>
        public IReadOnlyCollection<string> WireLog => this._wireLog;

        /// <summary>Gets or creates the per-session update capture for <paramref name="sessionId"/>.</summary>
        public SessionCapture GetOrAddCapture(string sessionId) => this._client!.GetOrAddCapture(sessionId);

        /// <inheritdoc/>
        public async ValueTask InitializeAsync()
        {
            try
            {
                this.StartMcpServer();
                this.StartAgentProcess();

                this._client = new RecordingAcpClient();
                Stream toAgent = new LineTeeStream(this._agentProcess!.StandardInput.BaseStream, isReadable: false, line => this._wireLog.Enqueue($"-> {line}"));
                Stream fromAgent = new LineTeeStream(this._agentProcess!.StandardOutput.BaseStream, isReadable: true, line => this._wireLog.Enqueue($"<- {line}"));

                this.Connection = Connection.RunClient(this._client, toAgent, fromAgent)
                    ?? throw new InvalidOperationException("dotacp.client.Connection.RunClient returned null.");

                InitializeResponse initializeResponse = await WaitWithTimeoutAsync(
                    this.Connection.InitializeAsync(
                        new InitializeRequest
                        {
                            ProtocolVersion = new ProtocolVersion(1),
                            ClientCapabilities = new ClientCapabilities
                            {
                                Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false },
                                Terminal = false,
                            },
                            ClientInfo = new Implementation { Name = "Agency.Acp.Test", Version = "1.0.0" },
                        },
                        CancellationToken.None),
                    TimeSpan.FromSeconds(30),
                    "initialize",
                    this);

                if (initializeResponse.AgentCapabilities.LoadSession)
                {
                    throw new InvalidOperationException("Expected supportsLoadSession: false per spec Ā§3.");
                }
            }
            catch
            {
                await this.DisposeAsync();
                throw;
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            try
            {
                this.Connection?.Dispose();
            }
            catch (Exception)
            {
                // Best-effort: the connection may already be broken (e.g. the process died); the
                // process-kill block below is what actually guarantees no orphaned agency-acp.
            }

            if (this._agentProcess is { } process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try
                        {
                            // EOF on stdin is what Program.RunAsync (spec E-15) treats as transport
                            // disconnect: it disposes every live session's graph and returns cleanly.
                            process.StandardInput.Close();
                        }
                        catch (Exception)
                        {
                            // Best-effort — fall through to the exit wait / hard kill below regardless.
                        }

                        if (!process.WaitForExit(10_000))
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(5_000);
                        }
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (this._mcpApp is { } app)
            {
                try
                {
                    await app.StopAsync();
                }
                catch (Exception)
                {
                    // Best-effort shutdown of the in-process test server; DisposeAsync below still runs.
                }

                await app.DisposeAsync();
            }
        }

        private void StartAgentProcess()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{this.AgentDllPath}\"",
                WorkingDirectory = Path.GetDirectoryName(this.AgentDllPath),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // The spawned process reads no appsettings.json of its own (none is copied next to
            // Agency.Acp.dll) — every Agent:* value it needs is supplied here as an environment
            // variable, per the box owner's exact endpoint/model (src/shared-test-appsettings.json).
            startInfo.Environment["Agent__DefaultClientName"] = "E2E-OpenAI";
            startInfo.Environment["Agent__DefaultModel"] = this.Model;
            startInfo.Environment["Agent__LLmClients__0__Name"] = "E2E-OpenAI";
            startInfo.Environment["Agent__LLmClients__0__ClientType"] = "OpenAI";
            startInfo.Environment["Agent__LLmClients__0__BaseUrl"] = this.BaseUrl;
            startInfo.Environment["Agent__LLmClients__0__ApiKey"] = this.ApiKey;

            var process = new Process { StartInfo = startInfo };
            process.Start();
            this._agentProcess = process;

            // Drain stderr continuously so the OS pipe buffer never fills and blocks the child
            // process; captured into the shared wire log for diagnostics on a test failure.
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    this._wireLog.Enqueue($"[stderr] {line}");
                }
            });
        }

        private void StartMcpServer()
        {
            this.BearerToken = Guid.NewGuid().ToString("n");

            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<HelpTools>();

            WebApplication app = builder.Build();

            // Requires the minted bearer token on every /mcp request (returns 401 without it — see
            // McpToolServer_RequiresBearerToken_Returns401WithoutIt), and — only once authenticated —
            // records which JSON-RPC method the request carried, so ListToolsInvoked/CallToolInvoked
            // above prove an AUTHENTICATED tools/list or tools/call reached this server.
            app.Use(async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments("/mcp"))
                {
                    await next(context);
                    return;
                }

                string? authorization = context.Request.Headers.Authorization;
                if (!string.Equals(authorization, $"Bearer {this.BearerToken}", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Unauthorized");
                    return;
                }

                if (HttpMethods.IsPost(context.Request.Method))
                {
                    context.Request.EnableBuffering();
                    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                    string body = await reader.ReadToEndAsync();
                    context.Request.Body.Position = 0;

                    string? method = TryExtractJsonRpcMethod(body);
                    switch (method)
                    {
                        case "tools/list":
                            this.ListToolsInvoked = true;
                            break;
                        case "tools/call":
                            this.CallToolInvoked = true;
                            break;
                    }
                }

                await next(context);
            });

            app.MapMcp("/mcp");

            app.StartAsync().GetAwaiter().GetResult();

            IServerAddressesFeature addressesFeature = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The MCP test server did not report a bound address.");
            string boundAddress = addressesFeature.Addresses.First();
            this.McpEndpoint = new Uri(new Uri(boundAddress), "/mcp").ToString();

            this._mcpApp = app;
        }

        private static string? TryExtractJsonRpcMethod(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return document.RootElement.TryGetProperty("method", out JsonElement methodProperty)
                    ? methodProperty.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ResolveAgentDllPath()
        {
            // Mirrors Agency.Harness.Console.Test.AgentConsoleTests.GetConsoleDll(): the test binary
            // sits at src/Acp/Agency.Acp.Test/bin/<cfg>/<tfm>/, and the agent binary (built as a
            // ProjectReference dependency) sits at src/Acp/Agency.Acp/bin/<cfg>/<tfm>/Agency.Acp.dll.
            string dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string[] parts = dir.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            string tfm = parts[^1];
            string cfg = parts[^2];
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", // up to src/Acp/
                "Agency.Acp",
                "bin", cfg, tfm,
                "Agency.Acp.dll"));
        }

        private static string GetRequired(IConfiguration configuration, string key) =>
            configuration[key] ?? throw new InvalidOperationException($"Missing required configuration: '{key}'.");
    }

    /// <summary>
    /// Per-session capture of everything <see cref="RecordingAcpClient"/> observes for one
    /// <c>sessionId</c>: streamed text (for O-1's ordering proof and O-3's "partial text retained"),
    /// the titles of every <c>tool_call</c> (for O-5), and an optional hook fired exactly once, on the
    /// very first streamed chunk (used by the cancel test to fire <c>session/cancel</c> genuinely
    /// mid-turn instead of racing a sleep).
    /// </summary>
    public sealed class SessionCapture
    {
        private int _firstChunkSeen;

        /// <summary>Gets every <c>agent_message_chunk</c> text fragment received so far, in order.</summary>
        public ConcurrentQueue<string> TextChunks { get; } = new();

        /// <summary>Gets the <c>Title</c> of every pending <c>tool_call</c> update received so far.</summary>
        public ConcurrentBag<string> ToolCallTitles { get; } = new();

        /// <summary>Gets whether at least one <c>agent_message_chunk</c> has been received.</summary>
        public bool HasFirstChunk => Volatile.Read(ref this._firstChunkSeen) != 0;

        /// <summary>Gets the <see cref="Stopwatch.GetTimestamp"/> value captured when the first chunk arrived.</summary>
        public long FirstChunkTimestamp { get; private set; }

        /// <summary>Gets or sets a callback invoked exactly once, synchronously, on the first chunk.</summary>
        public Action? OnFirstChunk { get; set; }

        /// <summary>Records one streamed text fragment, firing <see cref="OnFirstChunk"/> if this is the first.</summary>
        public void RecordChunk(string text)
        {
            this.TextChunks.Enqueue(text);
            if (Interlocked.Exchange(ref this._firstChunkSeen, 1) == 0)
            {
                this.FirstChunkTimestamp = Stopwatch.GetTimestamp();
                this.OnFirstChunk?.Invoke();
            }
        }

        /// <summary>Gets every captured text fragment concatenated in receipt order.</summary>
        public string JoinedText() => string.Concat(this.TextChunks);
    }

    /// <summary>
    /// The scripted ACP client: implements <see cref="IAcpClient"/> purely to receive
    /// <c>session/update</c> notifications into a per-session <see cref="SessionCapture"/>. Every
    /// other client-facing call is unreachable in this test (fs/terminal capabilities are advertised
    /// as <see langword="false"/>, and <see cref="Agency.Acp.Permissions.PersonaPermissionEvaluator"/>
    /// never asks — spec Ā§6.6), so each is implemented to fail loudly rather than hang if that
    /// assumption is ever wrong.
    /// </summary>
    private sealed class RecordingAcpClient : IAcpClient
    {
        private readonly ConcurrentDictionary<string, SessionCapture> _sessions = new(StringComparer.Ordinal);

        /// <summary>Gets or creates the capture for <paramref name="sessionId"/>.</summary>
        public SessionCapture GetOrAddCapture(string sessionId) =>
            this._sessions.GetOrAdd(sessionId, static _ => new SessionCapture());

        /// <inheritdoc/>
        public Task SessionUpdateAsync(SessionNotification notification, CancellationToken cancellationToken = default)
        {
            SessionCapture capture = this.GetOrAddCapture(notification.SessionId);
            switch (notification.Update)
            {
                case SessionUpdateAgentMessageChunk { Content: TextContent text }:
                    capture.RecordChunk(text.Text);
                    break;

                case ToolCall toolCall:
                    capture.ToolCallTitles.Add(toolCall.Title ?? string.Empty);
                    break;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<RequestPermissionResponse> RequestPermissionAsync(RequestPermissionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<RequestPermissionResponse>(new InvalidOperationException(
                "session/request_permission is unreachable in v1: PersonaPermissionEvaluator never returns Ask (spec Ā§6.6)."));

        /// <inheritdoc/>
        public Task<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<ReadTextFileResponse>(new NotSupportedException("fs.readTextFile: not advertised by this client."));

        /// <inheritdoc/>
        public Task<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<WriteTextFileResponse>(new NotSupportedException("fs.writeTextFile: not advertised by this client."));

        /// <inheritdoc/>
        public Task<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<CreateTerminalResponse>(new NotSupportedException("terminal: not advertised by this client."));

        /// <inheritdoc/>
        public Task<KillTerminalResponse> KillTerminalAsync(KillTerminalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<KillTerminalResponse>(new NotSupportedException("terminal: not advertised by this client."));

        /// <inheritdoc/>
        public Task<TerminalOutputResponse> TerminalOutputAsync(TerminalOutputRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<TerminalOutputResponse>(new NotSupportedException("terminal: not advertised by this client."));

        /// <inheritdoc/>
        public Task<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<ReleaseTerminalResponse>(new NotSupportedException("terminal: not advertised by this client."));

        /// <inheritdoc/>
        public Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken cancellationToken = default) =>
            Task.FromException<WaitForTerminalExitResponse>(new NotSupportedException("terminal: not advertised by this client."));

        /// <inheritdoc/>
        public Task<object> ExtMethodAsync(string method, object request, CancellationToken cancellationToken = default) =>
            Task.FromException<object>(new NotSupportedException($"Extension method '{method}' is not supported by this test client."));

        /// <inheritdoc/>
        public Task ExtNotificationAsync(string method, object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        /// <inheritdoc/>
        public void OnDisconnected(Connection connection)
        {
            // No-op: AcpEndToEndFixture.DisposeAsync drives shutdown explicitly and does not rely on
            // this callback to detect or react to disconnection.
        }
    }

    /// <summary>
    /// The one trivial MCP tool exposed to the model (spec Ā§13's <c>get_help</c> example, U-2).
    /// Deterministic and side-effect-free, so a successful call is trivial to verify from its
    /// returned text, and simple enough for a small local model to plausibly call.
    /// </summary>
    [McpServerToolType]
    private sealed class HelpTools
    {
        /// <summary>Returns a short, fixed help string for <paramref name="topic"/>.</summary>
        [McpServerTool(Name = "get_help")]
        [Description("Returns a short help string for the given topic. Call this whenever asked for help about a topic.")]
        public static string GetHelp([Description("The topic to get help about.")] string topic) =>
            $"Help for '{topic}': the answer is 42.";
    }

    /// <summary>
    /// Wraps one direction of the spawned process's stdio pipes, forwarding every byte unchanged
    /// while also splitting the traffic on newlines and reporting each complete line — the literal
    /// NDJSON frames <c>dotacp.client</c> sends/receives — to <paramref name="onLine"/>. Used purely
    /// to capture a faithful wire-level transcript for diagnostics and for this task's report; it
    /// does not alter the bytes exchanged in any way.
    /// </summary>
    private sealed class LineTeeStream(Stream inner, bool isReadable, Action<string> onLine) : Stream
    {
        private readonly List<byte> _lineBuffer = [];

        /// <inheritdoc/>
        public override bool CanRead => isReadable;

        /// <inheritdoc/>
        public override bool CanWrite => !isReadable;

        /// <inheritdoc/>
        public override bool CanSeek => false;

        /// <inheritdoc/>
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc/>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            this.Capture(buffer.Span[..read]);
            return read;
        }

        /// <inheritdoc/>
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            this.Capture(buffer.Span);
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count) =>
            this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) =>
            this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc/>
        public override void Flush() => inner.Flush();

        /// <inheritdoc/>
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private void Capture(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                if (b == (byte)'\n')
                {
                    string line = Encoding.UTF8.GetString([.. this._lineBuffer]).TrimEnd('\r');
                    this._lineBuffer.Clear();
                    if (line.Length > 0)
                    {
                        onLine(line);
                    }
                }
                else
                {
                    this._lineBuffer.Add(b);
                }
            }
        }
    }
}
