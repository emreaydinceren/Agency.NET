using System.Reflection;
using Agency.Acp.Errors;
using Agency.Acp.Sessions;
using Agency.Acp.Turns;
using Agency.Harness;
using Agency.Llm.Common;
using dotacp.protocol;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Dispatch;

/// <summary>
/// Routes incoming JSON-RPC 2.0 lines to the handful of <see cref="AgentMethods"/> this agent
/// supports in v1, and maps everything else — unknown methods, unparseable requests, malformed
/// params — to the matching JSON-RPC error per the ACP/JSON-RPC error code table.
/// </summary>
internal sealed class MethodDispatcher
{
    private static SessionRegistry _sessions = new();
    private static SessionFactory? _sessionFactory;
    private static TurnDriver? _turnDriver;

    private static readonly Dictionary<string, Func<JObject?, CancellationToken, Task<object?>>> Handlers =
        new(StringComparer.Ordinal)
        {
            [AgentMethods.Initialize] = HandleInitializeAsync,
            [AgentMethods.SessionNew] = HandleSessionNewAsync,
            [AgentMethods.SessionPrompt] = HandleSessionPromptAsync,
            [AgentMethods.SessionCancel] = HandleSessionCancelAsync,
            [AgentMethods.SessionSetConfigOption] = HandleSessionSetConfigOptionAsync,
            [AgentMethods.SessionClose] = HandleSessionCloseAsync,
            [AgentMethods.SessionDelete] = HandleSessionDeleteAsync,
        };

    /// <summary>
    /// Gets the process-wide <see cref="SessionRegistry"/>. Exposed so <see cref="Program"/> can
    /// drain every live session on transport disconnect (spec E-15) without the transport itself
    /// knowing anything about sessions.
    /// </summary>
    internal static SessionRegistry Sessions => _sessions;

    /// <summary>
    /// Wires the session-lifecycle handlers to a real <paramref name="sessions"/> registry and
    /// <paramref name="sessionFactory"/>. Called once by <see cref="Program"/> at startup; unit
    /// tests that never exercise <c>session/new</c> do not need to call this — the default empty
    /// registry is enough for <see cref="Sessions"/> to be well-defined either way.
    /// </summary>
    internal static void Configure(SessionRegistry sessions, SessionFactory sessionFactory)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    /// <summary>
    /// Wires <c>session/prompt</c> to a real <see cref="TurnDriver"/> built over
    /// <paramref name="client"/>. Called once by <see cref="Program"/> once the process's
    /// <see cref="Transport.StdioTransport"/> exists — <c>session/prompt</c> returns
    /// <see cref="ErrorCode.InternalError"/> until this has run.
    /// </summary>
    internal static void ConfigureClient(IAcpClientProxy client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _turnDriver = new TurnDriver(client);
    }

    /// <summary>
    /// Parses <paramref name="rawJson"/> as a single JSON-RPC request or notification, dispatches
    /// it, and returns the serialized JSON-RPC response line — or <see langword="null"/> when the
    /// input was a notification (no <c>id</c>) and therefore expects no response.
    /// </summary>
    public static async Task<string?> DispatchAsync(string rawJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawJson);

        JObject request;
        try
        {
            request = JObject.Parse(rawJson);
        }
        catch (JsonException)
        {
            return BuildError(id: null, ErrorCode.ParseError, "Invalid JSON.");
        }

        bool hasId = request.TryGetValue("id", out JToken? idToken);
        string? method = request.Value<string>("method");

        if (string.IsNullOrEmpty(method))
        {
            return hasId ? BuildError(idToken, ErrorCode.InvalidRequest, "Request is missing 'method'.") : null;
        }

        if (!Handlers.TryGetValue(method, out Func<JObject?, CancellationToken, Task<object?>>? handler))
        {
            return hasId ? BuildError(idToken, ErrorCode.MethodNotFound, $"Method '{method}' is not supported.") : null;
        }

        var @params = request["params"] as JObject;

        try
        {
            object? result = await handler(@params, cancellationToken).ConfigureAwait(false);
            return hasId ? BuildResult(idToken, result) : null;
        }
        catch (AcpJsonRpcException ex)
        {
            return hasId ? BuildError(idToken, ex.Code, ex.Message) : null;
        }
        catch (JsonException ex)
        {
            return hasId ? BuildError(idToken, ErrorCode.InvalidParams, $"Invalid params for '{method}': {ex.Message}") : null;
        }
    }

    private static Task<object?> HandleInitializeAsync(JObject? @params, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Deserializing validates the shape of the client's params (throws JsonException on a
        // malformed payload, mapped to InvalidParams by the caller); the values themselves are
        // not otherwise consulted — v1 always negotiates the same fixed capability set.
        @params?.ToObject<InitializeRequest>();

        var response = new InitializeResponse
        {
            ProtocolVersion = new ProtocolVersion(ProtocolMeta.Version),
            AuthMethods = [],
            AgentCapabilities = new AgentCapabilities
            {
                LoadSession = false,
            },
            AgentInfo = new Implementation
            {
                Name = "agency-acp",
                Version = GetAgentVersion(),
            },
        };

        return Task.FromResult<object?>(response);
    }

    /// <summary>
    /// Implements <c>session/new</c> (spec §8.1) via <see cref="SessionFactory"/>, then registers
    /// the resulting <see cref="SessionState"/> so <c>session/close</c>/<c>session/delete</c> can
    /// find it. The wire DTO (<see cref="NewSessionRequest"/>) carries no model field, so a
    /// client-requested model — spec §8.1 step 4 — is read from <c>_meta.model</c> when present.
    /// </summary>
    private static async Task<object?> HandleSessionNewAsync(JObject? @params, CancellationToken cancellationToken)
    {
        if (_sessionFactory is null)
        {
            throw new AcpJsonRpcException(ErrorCode.InternalError, "Session factory is not configured.");
        }

        NewSessionRequest request = @params?.ToObject<NewSessionRequest>() ?? new NewSessionRequest();
        string? requestedModelId = @params?["_meta"]?["model"]?.Value<string>();

        (SessionState state, NewSessionResponse response) =
            await _sessionFactory.CreateAsync(request, requestedModelId, cancellationToken).ConfigureAwait(false);
        _sessions.Add(state);

        return response;
    }

    /// <summary>Implements <c>session/close</c>: disposes and unregisters the named session.</summary>
    private static async Task<object?> HandleSessionCloseAsync(JObject? @params, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        CloseSessionRequest request = @params?.ToObject<CloseSessionRequest>()
            ?? throw new AcpJsonRpcException(ErrorCode.InvalidParams, "'sessionId' is required.");

        await _sessions.CloseAsync(request.SessionId).ConfigureAwait(false);
        return new CloseSessionResponse();
    }

    /// <summary>
    /// Implements <c>session/delete</c>. There is no persistence in v1 (spec §7.1), so deleting a
    /// session is identical to closing it: dispose and unregister.
    /// </summary>
    private static async Task<object?> HandleSessionDeleteAsync(JObject? @params, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        DeleteSessionRequest request = @params?.ToObject<DeleteSessionRequest>()
            ?? throw new AcpJsonRpcException(ErrorCode.InvalidParams, "'sessionId' is required.");

        await _sessions.CloseAsync(request.SessionId).ConfigureAwait(false);
        return new DeleteSessionResponse();
    }

    /// <summary>
    /// Implements <c>session/prompt</c> (spec §6.4, §8.2) by delegating the whole turn — including
    /// any permission park/resume loop — to <see cref="TurnDriver"/>.
    /// </summary>
    private static async Task<object?> HandleSessionPromptAsync(JObject? @params, CancellationToken cancellationToken)
    {
        if (_turnDriver is null)
        {
            throw new AcpJsonRpcException(ErrorCode.InternalError, "Turn driver is not configured.");
        }

        PromptRequest request = @params?.ToObject<PromptRequest>()
            ?? throw new AcpJsonRpcException(ErrorCode.InvalidParams, "'sessionId' and 'prompt' are required.");

        SessionState session = _sessions.GetRequired(request.SessionId);
        return await _turnDriver.RunTurnAsync(session, request.Prompt ?? [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Implements <c>session/cancel</c> (spec §6.4, E-13): cancels the named session's in-flight
    /// turn, if any. A notification (no <c>id</c>), so an unknown session id or an idle session is
    /// a silent no-op rather than an error — there is no request id to attach one to, and E-13
    /// requires "nothing in flight" to be a no-op regardless.
    /// </summary>
    private static Task<object?> HandleSessionCancelAsync(JObject? @params, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        CancelNotification request = @params?.ToObject<CancelNotification>()
            ?? throw new AcpJsonRpcException(ErrorCode.InvalidParams, "'sessionId' is required.");

        if (_sessions.TryGet(request.SessionId, out SessionState? state))
        {
            state!.TurnCts?.Cancel();
        }

        return Task.FromResult<object?>(null);
    }

    /// <summary>
    /// Implements <c>session/set_config_option</c> for the "model" and "effort" config ids (spec
    /// §6.2, §6.7): rebuilds the session's <see cref="Agent"/> via <see cref="IAgentFactory"/> and
    /// calls <see cref="ChatSession.SetAgent"/>, which preserves conversation history — the same
    /// path a model change takes (spec §16 G-4).
    /// </summary>
    private static async Task<object?> HandleSessionSetConfigOptionAsync(JObject? @params, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (_sessionFactory is null)
        {
            throw new AcpJsonRpcException(ErrorCode.InternalError, "Session factory is not configured.");
        }

        SetSessionConfigOptionRequest request = @params?.ToObject<SetSessionConfigOptionRequest>()
            ?? throw new AcpJsonRpcException(ErrorCode.InvalidParams, "'sessionId', 'configId' and 'value' are required.");

        SessionState session = _sessions.GetRequired(request.SessionId);
        string configId = request.ConfigId;

        if (!request.Value.TryGetSessionConfigValueId(out SessionConfigValueId valueId))
        {
            throw new AcpJsonRpcException(ErrorCode.InvalidParams, $"Config option '{configId}' requires a select value.");
        }

        string value = valueId;
        switch (configId)
        {
            case "model":
                session.ModelId = value;
                break;
            case "effort":
                session.EffortId = value;
                break;
            default:
                throw new AcpJsonRpcException(ErrorCode.InvalidParams, $"Unknown config option '{configId}'.");
        }

        // The effort ladder is re-applied on every rebuild — not only when "effort" itself changed —
        // so a model swap re-sends the (unchanged) effort setting to the new client, per spec §6.7 /
        // U-4: "the effort ladder is re-sent for the rebuilt client, and is unchanged unless the
        // surface changed". BuildEffortTransform is null when no effort is selected or the default
        // client's dialect has no ladder, in which case CreateAgent behaves exactly as before.
        IAgentFactory agentFactory = session.Scope.ServiceProvider.GetRequiredService<IAgentFactory>();
        Func<LlmClientOptions, LlmClientOptions>? effortTransform = _sessionFactory.BuildEffortTransform(session.EffortId);
        Agent newAgent = agentFactory.CreateAgent(null, session.ModelId, effortTransform);
        session.ChatSession.SetAgent(newAgent);

        return new SetSessionConfigOptionResponse
        {
            ConfigOptions = _sessionFactory.BuildConfigOptions(session.Catalogue, session.ModelId ?? string.Empty),
        };
    }

    private static string GetAgentVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";

    private static string BuildResult(JToken? id, object? result)
    {
        var envelope = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["result"] = result is null ? JValue.CreateNull() : JObject.FromObject(result),
        };
        return envelope.ToString(Formatting.None);
    }

    private static string BuildError(JToken? id, ErrorCode code, string message)
    {
        var envelope = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = JObject.FromObject(new Error { Code = code, Message = message }),
        };
        return envelope.ToString(Formatting.None);
    }
}
