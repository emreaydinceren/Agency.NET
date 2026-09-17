
using Agency.Llm.Claude;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Harness;

/// <summary>
/// Discovers the models available across the configured LLM clients and creates
/// <see cref="IChatClient"/> instances for a named client.
/// </summary>
public sealed partial class Models
{
    /// <summary>Name of the <see cref="ActivitySource"/> used for model-discovery tracing spans.</summary>
    public const string ActivitySourceName = "Agency.Harness.Models";

    /// <summary>Name of the <see cref="Meter"/> used for model-discovery metrics.</summary>
    public const string MeterName = "Agency.Harness.Models";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _requestCounter = _meter.CreateCounter<long>(
        "models.requests",
        description: "Total number of model discovery requests");

    private static readonly Counter<long> _errorCounter = _meter.CreateCounter<long>(
        "models.errors",
        description: "Total number of failed model discovery requests");

    private static readonly Counter<long> _modelCounter = _meter.CreateCounter<long>(
        "models.returned",
        description: "Number of models returned by providers");

    private static readonly Histogram<double> _durationHistogram = _meter.CreateHistogram<double>(
        "models.duration",
        unit: "ms",
        description: "Duration of model discovery requests in milliseconds");

    private readonly IOptions<AgentOptions> _agentOptions;

    private readonly ILogger<Models> _logger;

    private readonly ILoggerFactory? _loggerFactory;

    private IEnumerable<LlmClientOptions> _llmClientOptions => this._agentOptions.Value.LLmClients;

    /// <param name="agentOptions">Supplies the configured <see cref="LlmClientOptions"/> to discover models from and create clients for.</param>
    /// <param name="logger">Optional structured logger; defaults to <see cref="NullLogger{T}.Instance"/>.</param>
    /// <param name="loggerFactory">Passed through to created clients (e.g. so <see cref="OpenAIClient"/> can log failed HTTP requests); optional.</param>
    public Models(IOptions<AgentOptions> agentOptions, ILogger<Models>? logger = null, ILoggerFactory? loggerFactory = null)
    {
        this._agentOptions = agentOptions ?? throw new ArgumentNullException(nameof(agentOptions));
        this._logger = logger ?? NullLogger<Models>.Instance;
        this._loggerFactory = loggerFactory;
    }

    /// <summary>Queries every configured LLM client for its available models.</summary>
    /// <param name="cancellationToken">Token used to stop discovery early; a client request already in flight still completes.</param>
    /// <returns>The discovered models, grouped by the <see cref="LlmClientOptions"/> of the client that returned them.</returns>
    public async Task<IEnumerable<IGrouping<LlmClientOptions, Model>>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity(nameof(GetAllAsync));
        var llmClientOptions = this._llmClientOptions.ToList();

        activity?.SetTag("agentic.models.client_count", llmClientOptions.Count);

        var tags = new TagList
        {
            { "agentic.models.operation", nameof(GetAllAsync) },
        };

        _requestCounter.Add(1, tags);

        var sw = Stopwatch.StartNew();
        var modelCount = 0;
        var pairs = new List<(LlmClientOptions, Model)>();

        this.LogStartingModelDiscovery(llmClientOptions.Count);

        try
        {
            foreach (var option in llmClientOptions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                this.LogFetchingModelsFromClient(option.Name, option.ClientType);

                IModelProvider provider = CreateModelProvider(option);

                foreach (var model in await provider.GetModelsAsync(cancellationToken))
                {
                    pairs.Add((option, model));
                    modelCount++;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            _modelCounter.Add(modelCount, tags);

            activity?.SetTag("agentic.models.returned_models", modelCount);
            activity?.SetTag("agentic.models.client_count", llmClientOptions.Count);

            this.LogModelDiscoveryCompleted(llmClientOptions.Count, modelCount, sw.Elapsed.TotalMilliseconds);

            return pairs.GroupBy(s => s.Item1, s => s.Item2);
        }
        catch (Exception ex)
        {
            _errorCounter.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            this.LogModelDiscoveryFailed(ex);
            throw;
        }
        finally
        {
            sw.Stop();
            _durationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);
        }
    }

    /// <summary>
    /// Creates an <see cref="IChatClient"/> for the named provider and returns the provider's
    /// display name alongside the client (used in telemetry tags).
    /// </summary>
    public (IChatClient Client, string ClientType) CreateChatClient(string clientName) =>
        this.CreateChatClient(clientName, configureOptions: null);

    /// <summary>
    /// Resolves <paramref name="clientName"/>'s configured <see cref="LlmClientOptions"/>, applies
    /// <paramref name="configureOptions"/> when supplied, and builds the client from the result.
    /// Backs <see cref="AgentFactory"/>'s per-client-effort overload of
    /// <see cref="IAgentFactory.CreateAgent(string?, string?, Func{LlmClientOptions, LlmClientOptions}?)"/>
    /// (spec §6.7); internal because nothing outside this assembly needs the transform seam.
    /// </summary>
    internal (IChatClient Client, string ClientType) CreateChatClient(
        string clientName, Func<LlmClientOptions, LlmClientOptions>? configureOptions)
    {
        using var activity = _activitySource.StartActivity(nameof(CreateChatClient));
        activity?.SetTag("agentic.models.client_name", clientName);

        foreach (var options in this._llmClientOptions)
        {
            if (options.Name.Equals(clientName, StringComparison.OrdinalIgnoreCase))
            {
                LlmClientOptions effective = configureOptions is null ? options : configureOptions(options);
                activity?.SetTag("agentic.models.client_type", effective.ClientType);
                this.LogResolvedClient(options.Name, effective.ClientType);
                return CreateChatClient(effective, this._loggerFactory);
            }
        }

        activity?.SetStatus(ActivityStatusCode.Error, "Client configuration not found");
        this.LogClientConfigNotFound(clientName);
        throw new InvalidOperationException($"No LLM client configuration found with name '{clientName}'.");
    }

    /// <summary>
    /// Dispatches on <see cref="LlmClientOptions.ClientType"/> to build the right provider client
    /// for <paramref name="options"/>. The single provider switch backing both
    /// <see cref="CreateChatClient(string)"/> (by configured client name) and any caller that
    /// already has a (possibly modified, e.g. <c>with { SuppressThinking = true }</c>)
    /// <see cref="LlmClientOptions"/> in hand and needs to build its client without duplicating
    /// this switch (e.g. <c>Agency.Harness.Console</c>'s consolidator/distiller client bootstrap).
    /// </summary>
    /// <param name="options">The resolved (or ad hoc) client options to build a client for.</param>
    /// <param name="loggerFactory">Passed through to <see cref="OpenAIClient"/> for HTTP failure logging; optional.</param>
    internal static (IChatClient Client, string ClientType) CreateChatClient(LlmClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        return options.ClientType.ToUpperInvariant() switch
        {
            "CLAUDE" => (new ClaudeClient(options).CreateChatClient(), "Claude"),
            "OPENAI" => (new OpenAIClient(options, loggerFactory).CreateChatClient(), "OpenAI"),
            _ => throw new InvalidOperationException($"Unsupported provider '{options.ClientType}'."),
        };
    }

    private static IModelProvider CreateModelProvider(LlmClientOptions options)
    {
        return options.ClientType.ToUpperInvariant() switch
        {
            "CLAUDE" => new ClaudeClient(options),
            "OPENAI" => new OpenAIClient(options),
            _ => throw new InvalidOperationException($"Unsupported provider '{options.ClientType}'."),
        };
    }

    /// <summary>Logs that model discovery is starting across the configured LLM clients.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting model discovery across {ClientCount} configured LLM clients.")]
    private partial void LogStartingModelDiscovery(int clientCount);

    /// <summary>Logs that models are being fetched from a specific LLM client.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Fetching models from LLM client {ClientName} ({ClientType}).")]
    private partial void LogFetchingModelsFromClient(string clientName, string clientType);

    /// <summary>Logs that model discovery completed across all configured clients.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Completed model discovery across {ClientCount} clients. Models={ModelCount}, DurationMs={DurationMs}")]
    private partial void LogModelDiscoveryCompleted(int clientCount, int modelCount, double durationMs);

    /// <summary>Logs that model discovery failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Model discovery failed.")]
    private partial void LogModelDiscoveryFailed(Exception ex);

    /// <summary>Logs that an LLM client was resolved by name.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Resolved LLM client {ClientName} ({ClientType}).")]
    private partial void LogResolvedClient(string clientName, string clientType);

    /// <summary>Logs that no LLM client configuration was found with the given name.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "No LLM client configuration found with name {ClientName}.")]
    private partial void LogClientConfigNotFound(string clientName);
}
