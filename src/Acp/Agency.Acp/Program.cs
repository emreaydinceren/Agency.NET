using System.Globalization;
using Agency.Acp.Dispatch;
using Agency.Acp.Permissions;
using Agency.Acp.Sessions;
using Agency.Acp.Transport;
using Agency.Acp.Turns;
using Agency.Harness;
using Agency.Harness.Permissions;
using Agency.Llm.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Agency.Acp;

/// <summary>
/// Entry point and composition root for the Agency ACP host process.
///
/// stdout carries JSON-RPC protocol bytes only (spec E-4, high severity): every log sink is
/// configured to write to a file, never to the console, and <see cref="System.Console.Out"/> is
/// replaced with a no-op writer before the transport starts, so a stray write from anywhere in the
/// process — including a dependency this code does not control — cannot reach the protocol stream.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the ACP host: speaks newline-delimited JSON-RPC 2.0 over stdio.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        _ = args;

        // Capture the real stdio handles before Console.Out is neutralized below.
        Stream realStandardInput = Console.OpenStandardInput();
        Stream realStandardOutput = Console.OpenStandardOutput();

        string logFilePath = Path.Combine(AppContext.BaseDirectory, "logs", "agency-acp-.log");
        ConfigureLogging(logFilePath);

        // Guard against any stray Console.WriteLine — from this process or a dependency — reaching
        // the JSON-RPC stream. All writes to the protocol go through StdioTransport directly on the
        // raw stream captured above, never through Console.Out.
        Console.SetOut(TextWriter.Null);

        using IHost host = BuildHost();
        ConfigureSessionLifecycle(host.Services);

        try
        {
            return await RunAsync(realStandardInput, realStandardOutput, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the composition root: configuration (shared config + environment variables, per the
    /// same placeholder-resolution convention as <c>Agency.Harness.Console</c>) plus the harness's
    /// own <c>AddAgencyAgent()</c> registrations. No hosted services are registered — this is used
    /// purely as a DI/config container, never run.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so <c>Agency.Acp.Test.V1GuaranteeTests</c> can build the exact
    /// production composition and assert against it, instead of a parallel copy that could drift.
    /// </remarks>
    internal static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddSharedConfiguration();

        // Lock #4 (spec §6.5, V1GuaranteeTests.SkillShellExecutionDisabled): shell-directive
        // (`!cmd`) expansion in SKILL.md bodies is disabled unconditionally for the v1 ACP host.
        // Added as its own source — after the shared config, before the placeholder resolver — so
        // no config file or environment variable can silently re-enable it; the skill tool is not
        // even registered (lock #2), but this holds regardless of that as defense in depth.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Skills:DisableShellExecution"] = "true",
        });

        builder.Configuration.AddPlaceholderResolver();

        builder.Services.AddOptions<AgentOptions>().BindConfiguration("Agent");

        // Lock #3 (spec §6.5, V1GuaranteeTests.NoShellRunnerWired): AddAgencyAgent() registers no
        // ISkillShellRunner (Agency.Harness.Console is the only host that wires
        // PowerShellSkillShellRunner), and this composition root registers none either.
        //
        // Lock #5 (spec §6.5, V1GuaranteeTests.NoHookCanReturnAsk): AddAgencyConfiguredHooks() is
        // deliberately never called here — the only source of a hook-side Ask is a configured
        // OnPreToolUse hook, and none exists. Per spec §14 this cannot be proven statically (a
        // future Ask could slip into a rewrite hook just as easily as a legitimate one), so the
        // guarantee is behavioural, not structural — see the test's own remarks. (The evaluator
        // registered below never returns Ask either — see PersonaPermissionEvaluator.)
        builder.Services.AddAgencyAgent();

        // Lock #6 (spec §6.6, V1GuaranteeTests.PermissionEvaluatorIsWired): the v1 permission gate
        // — allow this session's granted tools, deny everything else, never ask — must actually
        // reach the Agent SessionFactory builds, not just exist as a tested-but-unwired type. Scoped
        // (one instance per session, since each session's granted tool set differs — a singleton
        // here would leak one session's tools into every other session). Necessarily constructed
        // with an EMPTY granted set: this resolves before any given session's McpClientPool exists,
        // so SessionFactory.CreateAsync calls SetGrantedTools once the pool has connected and the
        // session's real tool names are known — before any turn can run.
        builder.Services.AddScoped<IPermissionEvaluator>(_ => new PersonaPermissionEvaluator([]));

        return builder.Build();
    }

    /// <summary>
    /// Wires <see cref="MethodDispatcher"/> to a real <see cref="SessionRegistry"/> and
    /// <see cref="SessionFactory"/> so <c>session/new</c>, <c>session/close</c>, and
    /// <c>session/delete</c> operate on the object graph described by spec §7.2, instead of the
    /// <c>NotImplementedStub</c> D6 left in place.
    /// </summary>
    private static void ConfigureSessionLifecycle(IServiceProvider services)
    {
        AgentOptions processOptions = services.GetRequiredService<IOptions<AgentOptions>>().Value;
        IServiceScopeFactory scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        Models models = services.GetRequiredService<Models>();

        async Task<IReadOnlyList<Model>> FetchCatalogueAsync(CancellationToken ct)
        {
            var byClient = await models.GetAllAsync(ct).ConfigureAwait(false);
            return byClient.SelectMany(group => group).ToList();
        }

        var sessionFactory = new SessionFactory(scopeFactory, processOptions, FetchCatalogueAsync);
        MethodDispatcher.Configure(new SessionRegistry(), sessionFactory);
    }

    /// <summary>
    /// Configures the process-wide Serilog logger to write to <paramref name="logFilePath"/> only.
    /// Exposed internally so tests can verify no sink targets the console.
    /// </summary>
    internal static void ConfigureLogging(string logFilePath)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Infinite, formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    /// <summary>
    /// Runs the reader/writer loop over <paramref name="input"/> and <paramref name="output"/>
    /// until end-of-stream or cancellation, dispatching every line through <see cref="MethodDispatcher"/>.
    /// </summary>
    internal static async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        await using var transport = new StdioTransport(input, output);

        // The same transport carries both directions: incoming client requests/notifications, and
        // responses to this proxy's own outgoing session/request_permission calls (spec §6.4). The
        // latter must be intercepted before MethodDispatcher ever sees them (see TryHandleAsResponse).
        var clientProxy = new AcpClientProxy(transport);
        MethodDispatcher.ConfigureClient(clientProxy);

        Log.Verbose("Agency.Acp stdio loop starting.");

        await transport.RunAsync(async (line, lineCt) =>
        {
            Log.Verbose("Received line: {Line}", line);

            if (TryHandleAsResponse(line, clientProxy))
            {
                return;
            }

            string? response = await MethodDispatcher.DispatchAsync(line, lineCt).ConfigureAwait(false);
            if (response is not null)
            {
                Log.Verbose("Sending response: {Response}", response);
                await transport.SendAsync(response, lineCt).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);

        Log.Verbose("Agency.Acp stdio loop ended (end of input).");

        // Transport disconnect (spec E-15): the first real client never sends session/close, so
        // reaching end-of-input — not a wire message — is what must dispose every live session's
        // McpClientPool and DI scope. session/close (already routed through the same registry)
        // only releases sooner; disposal here is idempotent per session either way.
        await MethodDispatcher.Sessions.DisposeAllAsync().ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Attempts to complete a pending <see cref="AcpClientProxy.RequestPermissionAsync"/> call with
    /// <paramref name="line"/>. Returns <see langword="false"/> for an unparseable line or any line
    /// that is not a response to one of the proxy's own outgoing requests, in which case the caller
    /// must still hand it to <see cref="MethodDispatcher"/> as usual.
    /// </summary>
    private static bool TryHandleAsResponse(string line, AcpClientProxy clientProxy)
    {
        try
        {
            return clientProxy.TryHandleResponse(JObject.Parse(line));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
