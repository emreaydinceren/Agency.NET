using Agency.Acp.Errors;
using Agency.Acp.Permissions;
using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;
using Agency.Harness.Tools;
using Agency.Llm.Common;
using dotacp.protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Acp.Sessions;

/// <summary>
/// Implements session creation (spec §8.1): resolves the model catalogue, picks a model and
/// builds a per-session <see cref="AgentOptions"/>, connects the requested MCP servers, and
/// assembles the per-session object graph described by <see cref="SessionState"/>.
/// </summary>
/// <remarks>
/// Two steps are deliberately fail-soft rather than fail-hard, per spec §8.1: an unreachable
/// model catalogue degrades to an empty list (step 2), and a requested model absent from the
/// catalogue degrades to <see cref="AgentOptions.DefaultModel"/> (step 4) — never a JSON-RPC
/// error. An unreachable MCP server is likewise recorded in <see cref="McpClientPool.FailedServers"/>
/// rather than thrown (step 7), because <see cref="McpClientPool.CreateAsync"/> already fails soft
/// per server.
/// </remarks>
internal sealed class SessionFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentOptions _processOptions;
    private readonly Func<CancellationToken, Task<IReadOnlyList<Model>>> _catalogueFetcher;

    /// <param name="scopeFactory">Used to give each session its own DI scope (spec §6.3).</param>
    /// <param name="processOptions">
    /// The process-wide <see cref="AgentOptions"/> (from configuration). Never mutated; each
    /// session clones it (see <see cref="SessionState.Options"/>).
    /// </param>
    /// <param name="catalogueFetcher">
    /// Resolves the model catalogue (spec §8.1 step 2 — "one GET"). Exceptions are swallowed by
    /// this class, not the fetcher, so callers may let provider errors propagate.
    /// </param>
    public SessionFactory(
        IServiceScopeFactory scopeFactory,
        AgentOptions processOptions,
        Func<CancellationToken, Task<IReadOnlyList<Model>>> catalogueFetcher)
    {
        this._scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this._processOptions = processOptions ?? throw new ArgumentNullException(nameof(processOptions));
        this._catalogueFetcher = catalogueFetcher ?? throw new ArgumentNullException(nameof(catalogueFetcher));
    }

    /// <summary>
    /// Runs the session-creation algorithm and returns the new <see cref="SessionState"/> together
    /// with the <see cref="NewSessionResponse"/> to send back to the client.
    /// </summary>
    /// <param name="request">The deserialized <c>session/new</c> request.</param>
    /// <param name="requestedModelId">
    /// An optional client-requested model id. Not part of the wire DTO (<c>dotacp.protocol</c>'s
    /// <see cref="NewSessionRequest"/> carries no model field); the caller is responsible for
    /// extracting it from wherever the client places it (e.g. <see cref="NewSessionRequest.Meta"/>).
    /// <see langword="null"/> when the client expressed no preference.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(SessionState State, NewSessionResponse Response)> CreateAsync(
        NewSessionRequest request,
        string? requestedModelId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Step 1 — validate mcpServers[] shapes. Only Http transport is supported in v1 (spec §6.5);
        // anything else, or an Http entry missing its required fields, is a malformed request.
        McpServerConfig[] serverConfigs = ValidateAndMapServers(request.McpServers);

        // Step 2 — resolve the catalogue. A failure of any kind (network, provider, whatever the
        // fetcher throws) degrades to an empty catalogue; it must never fail session creation.
        IReadOnlyList<Model> catalogue;
        try
        {
            catalogue = await this._catalogueFetcher(ct).ConfigureAwait(false);
        }
        catch
        {
            catalogue = [];
        }

        // Step 3 — filter Kind == embedding where known. Kind == null is retained: unknown must
        // never be treated as false (spec P3), so it cannot be excluded on this basis.
        List<Model> chatCatalogue = catalogue.Where(m => m.Kind != ModelKind.Embedding).ToList();

        // Step 4 — select model: requested model wins only if it is actually in the (filtered)
        // catalogue; otherwise fall back to AgentOptions.DefaultModel. An unknown model id is never
        // an error (the G6 contract).
        Model? requestedModel = requestedModelId is { Length: > 0 }
            ? chatCatalogue.FirstOrDefault(m => string.Equals(m.Id, requestedModelId, StringComparison.Ordinal))
            : null;

        string selectedModelId = requestedModel?.Id ?? this._processOptions.DefaultModel
            ?? throw new AcpJsonRpcException(ErrorCode.InternalError, "Agent:DefaultModel is not configured.");

        Model? selectedCatalogueEntry = requestedModel
            ?? chatCatalogue.FirstOrDefault(m => string.Equals(m.Id, selectedModelId, StringComparison.Ordinal));

        // Step 5 — per-session AgentOptions: only ContextWindowSize varies (spec §6.3); everything
        // else (including process-wide identity/hooks) is shared per the documented v1 constraint.
        AgentOptions sessionOptions = CloneWithContextWindow(this._processOptions, selectedCatalogueEntry?.ContextLength);

        IServiceScope scope = this._scopeFactory.CreateScope();
        McpClientPool? mcpPool = null;
        try
        {
            // Step 6 — build the client + Agent for the selected model. Reuses the harness's own
            // IAgentFactory (client/model defaulting, hook folding) rather than re-implementing it.
            IAgentFactory agentFactory = scope.ServiceProvider.GetRequiredService<IAgentFactory>();
            Agent agent = agentFactory.CreateAgent(null, selectedModelId);

            // Step 7 — connect the client's MCP servers. Per-server failures are recorded on
            // McpClientPool.FailedServers, never thrown (fail-soft: fewer tools, not a failed session).
            mcpPool = await McpClientPool.CreateAsync(new McpClientOptions { Servers = serverConfigs }, ct: ct)
                .ConfigureAwait(false);

            // Permission gate (spec §6.6): allow this session's own granted tools, deny everything
            // else, never ask. Program.BuildHost registers Agency.Acp.Permissions.PersonaPermissionEvaluator
            // as a SCOPED IPermissionEvaluator with an empty granted set — necessarily empty at that
            // point, since it is constructed (via IAgentFactory, above) before this session's
            // McpClientPool exists. The same scoped instance `agentFactory` already captured is
            // resolved again here and populated with the real, now-known tool names — explicit and
            // derived from mcpPool.Tools, never implicit (V1GuaranteeTests.PermissionEvaluatorIsWired).
            // No turn can run before this point, so there is no window where the gate is live but stale.
            if (scope.ServiceProvider.GetService<IPermissionEvaluator>() is PersonaPermissionEvaluator personaEvaluator)
            {
                personaEvaluator.SetGrantedTools(mcpPool.Tools.Select(t => t.Definition.Name));
            }

            // Lock #1 & Lock #2 (spec §6.5, V1GuaranteeTests.NoBuiltInToolsRegistered /
            // .SkillToolNotRegistered): this is the only place a session's ToolRegistry is built,
            // and it is seeded from mcpPool.Tools alone — no built-in tool (read_file, write_file,
            // execute_powershell, subagent_tool) and no "skill" tool is ever added here.
            var toolContext = new ToolContext { Registry = new ToolRegistry(mcpPool.Tools) };
            var chatSession = new ChatSession(agent, sessionOptions, toolContext: toolContext);

            // Step 8 — register and respond.
            string sessionId = Guid.NewGuid().ToString("n");
            var state = new SessionState
            {
                SessionId = sessionId,
                ChatSession = chatSession,
                Options = sessionOptions,
                McpPool = mcpPool,
                Scope = scope,
                Catalogue = chatCatalogue,
                ModelId = selectedModelId,
                Cwd = request.Cwd,
            };

            NewSessionResponse response = new()
            {
                SessionId = sessionId,
                ConfigOptions = BuildConfigOptions(chatCatalogue, selectedModelId),
            };

            return (state, response);
        }
        catch
        {
            if (mcpPool is not null)
            {
                await mcpPool.DisposeAsync().ConfigureAwait(false);
            }

            scope.Dispose();
            throw;
        }
    }

    private static McpServerConfig[] ValidateAndMapServers(McpServer[]? servers)
    {
        if (servers is null || servers.Length == 0)
        {
            return [];
        }

        var mapped = new McpServerConfig[servers.Length];
        for (int i = 0; i < servers.Length; i++)
        {
            if (servers[i] is not McpServerHttp http)
            {
                throw new AcpJsonRpcException(
                    ErrorCode.InvalidParams,
                    $"mcpServers[{i}]: only Http transport is supported in v1.");
            }

            if (string.IsNullOrEmpty(http.Name) || string.IsNullOrEmpty(http.Url))
            {
                throw new AcpJsonRpcException(
                    ErrorCode.InvalidParams,
                    $"mcpServers[{i}]: 'name' and 'url' are required.");
            }

            mapped[i] = new McpServerConfig
            {
                Name = http.Name,
                Transport = McpTransportKind.Http,
                Url = http.Url,
                Headers = http.Headers?.ToDictionary(h => h.Name, h => h.Value),
            };
        }

        return mapped;
    }

    private static AgentOptions CloneWithContextWindow(AgentOptions source, int? catalogueContextLength) => new()
    {
        DefaultClientName = source.DefaultClientName,
        DefaultModel = source.DefaultModel,
        TurnTimeoutSeconds = source.TurnTimeoutSeconds,
        LLmClients = source.LLmClients,
        ContextWindowSize = catalogueContextLength ?? source.ContextWindowSize,
        ProgressiveDiscovery = source.ProgressiveDiscovery,
        LogToolPayloads = source.LogToolPayloads,
        UserId = source.UserId,
        BaselineHooks = source.BaselineHooks,
        UserHooks = source.UserHooks,
        ConfiguredHooks = source.ConfiguredHooks,
    };

    /// <summary>
    /// Maps the model catalogue and the effort ladder onto <see cref="SessionConfigOption"/>s.
    /// <see cref="SessionConfigOption"/> is a discriminated base (<c>type</c>: "select" | "boolean")
    /// with <see cref="SessionConfigSelect"/> and <c>SessionConfigBoolean</c> as its two concrete
    /// shapes; both model and effort are exposed as <see cref="SessionConfigSelect"/>: one option
    /// per model
    /// (<c>Value</c> = model id, <c>Name</c> = model name, <c>Description</c> = a point-in-time
    /// residency statement, <see langword="null"/> when unknown — spec P3/§6.7), and, only when the
    /// selected client's dialect supports it, one option per effort tier. When neither thinking
    /// dialect is supported the effort option is omitted entirely — an empty ladder beats a
    /// decorative one (spec §6.7).
    /// </summary>
    /// <remarks>
    /// Internal rather than private so <c>session/set_config_option</c> can rebuild the same
    /// config-option shape from a session's <see cref="SessionState.Catalogue"/> after a model or
    /// effort change, instead of a second, drifting implementation.
    /// </remarks>
    internal SessionConfigOption[] BuildConfigOptions(IReadOnlyList<Model> chatCatalogue, string selectedModelId)
    {
        var modelOptions = new SessionConfigSelectOption[chatCatalogue.Count];
        for (int i = 0; i < chatCatalogue.Count; i++)
        {
            Model m = chatCatalogue[i];
            modelOptions[i] = new SessionConfigSelectOption
            {
                Name = m.Name,
                Value = m.Id,
                Description = DescribeResidency(m.IsLoaded),
            };
        }

        var options = new List<SessionConfigOption>
        {
            new SessionConfigSelect
            {
                Id = "model",
                Name = "Model",
                Category = SessionConfigOptionCategory.Model,
                CurrentValue = selectedModelId,
                Options = new SessionConfigSelectOptions(modelOptions),
            },
        };

        string? selectedClientType = this.DefaultClientType();

        SessionConfigSelectOption[] effortOptions = BuildEffortLadder(selectedClientType);
        if (effortOptions.Length > 0)
        {
            options.Add(new SessionConfigSelect
            {
                Id = "effort",
                Name = "Effort",
                Category = SessionConfigOptionCategory.ThoughtLevel,
                CurrentValue = effortOptions[0].Value,
                Options = new SessionConfigSelectOptions(effortOptions),
            });
        }

        return options.ToArray();
    }

    private static string? DescribeResidency(bool? isLoaded) => isLoaded switch
    {
        true => "loaded now",
        false => "not loaded",
        null => null,
    };

    /// <summary>Gets the <c>ClientType</c> of the process's default LLM client (e.g. "Claude"), or <see langword="null"/> if not found.</summary>
    private string? DefaultClientType() =>
        this._processOptions.LLmClients
            .FirstOrDefault(c => string.Equals(c.Name, this._processOptions.DefaultClientName, StringComparison.OrdinalIgnoreCase))
            ?.ClientType;

    /// <summary>
    /// Builds the <see cref="LlmClientOptions"/> transform for <paramref name="effortId"/> against
    /// the default client's dialect (spec §6.7). Backs <c>session/set_config_option</c>'s "effort"
    /// case: this is what actually threads a chosen effort tier into client construction via
    /// <see cref="IAgentFactory.CreateAgent(string?, string?, Func{LlmClientOptions, LlmClientOptions}?)"/>,
    /// closing the gap where choosing a tier recorded an id but never reached the client.
    /// </summary>
    /// <param name="effortId">
    /// The tier id from <see cref="BuildEffortLadder"/> (e.g. "low"), or <see langword="null"/>/empty
    /// when no effort has been selected for this session.
    /// </param>
    /// <returns>
    /// A transform applying the tier's thinking settings, or <see langword="null"/> when no effort
    /// is selected, or the default client's dialect has no ladder (spec §6.7: an empty ladder beats
    /// a decorative one — never emits the rejected <c>reasoning_effort</c> mechanism either way).
    /// </returns>
    internal Func<LlmClientOptions, LlmClientOptions>? BuildEffortTransform(string? effortId)
    {
        if (string.IsNullOrEmpty(effortId))
        {
            return null;
        }

        // The Claude tier budgets (1024/4096/16384) are chosen defaults, not spec-mandated numbers —
        // spec §6.7 only requires the mechanism (thinking.budget_tokens / enable_thinking) and that
        // reasoning_effort is never emitted; it measures example behavior but names no specific
        // budget values. Treat these as reasonable starting points, not a protocol requirement.
        return (this.DefaultClientType()?.ToUpperInvariant(), effortId) switch
        {
            ("CLAUDE", "off") => static opts => opts with { EnableThinking = false, ThinkingBudgetTokens = null },
            ("CLAUDE", "low") => static opts => opts with { EnableThinking = true, ThinkingBudgetTokens = 1024 },
            ("CLAUDE", "medium") => static opts => opts with { EnableThinking = true, ThinkingBudgetTokens = 4096 },
            ("CLAUDE", "high") => static opts => opts with { EnableThinking = true, ThinkingBudgetTokens = 16384 },
            ("OPENAI", "off") => static opts => opts with { EnableThinking = false, ThinkingBudgetTokens = null },
            ("OPENAI", "on") => static opts => opts with { EnableThinking = true, ThinkingBudgetTokens = null },
            _ => null,
        };
    }

    /// <summary>
    /// Builds the effort ladder for the given client type (spec §6.7): four named tiers for the
    /// Claude-style <c>thinking.budget_tokens</c> dialect, two for the OpenAI-style
    /// <c>enable_thinking</c> dialect, and an empty ladder — not a decorative one — for anything else.
    /// </summary>
    private static SessionConfigSelectOption[] BuildEffortLadder(string? clientType) =>
        clientType?.ToUpperInvariant() switch
        {
            "CLAUDE" =>
            [
                new SessionConfigSelectOption { Name = "Off", Value = "off" },
                new SessionConfigSelectOption { Name = "Low", Value = "low" },
                new SessionConfigSelectOption { Name = "Medium", Value = "medium" },
                new SessionConfigSelectOption { Name = "High", Value = "high" },
            ],
            "OPENAI" =>
            [
                new SessionConfigSelectOption { Name = "Off", Value = "off" },
                new SessionConfigSelectOption { Name = "On", Value = "on" },
            ],
            _ => [],
        };
}
