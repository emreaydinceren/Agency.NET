
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
public sealed class Models
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

    private IEnumerable<LlmClientOptions> _llmClientOptions => this._agentOptions.Value.LLmClients;

    /// <param name="agentOptions">Supplies the configured <see cref="LlmClientOptions"/> to discover models from and create clients for.</param>
    /// <param name="logger">Optional structured logger; defaults to <see cref="NullLogger{T}.Instance"/>.</param>
    public Models(IOptions<AgentOptions> agentOptions, ILogger<Models>? logger = null)
    {
        this._agentOptions = agentOptions ?? throw new ArgumentNullException(nameof(agentOptions));
        this._logger = logger ?? NullLogger<Models>.Instance;
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

        this._logger.LogInformation("Starting model discovery across {ClientCount} configured LLM clients.", llmClientOptions.Count);

        try
        {
            foreach (var option in llmClientOptions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                this._logger.LogInformation("Fetching models from LLM client {ClientName} ({ClientType}).", option.Name, option.ClientType);

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

            this._logger.LogInformation(
                "Completed model discovery across {ClientCount} clients. Models={ModelCount}, DurationMs={DurationMs}",
                llmClientOptions.Count, modelCount, sw.Elapsed.TotalMilliseconds);

            return pairs.GroupBy(s => s.Item1, s => s.Item2);
        }
        catch (Exception ex)
        {
            _errorCounter.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            this._logger.LogError(ex, "Model discovery failed.");
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
    public (IChatClient Client, string ClientType) CreateChatClient(string clientName)
    {
        using var activity = _activitySource.StartActivity(nameof(CreateChatClient));
        activity?.SetTag("agentic.models.client_name", clientName);

        foreach (var options in this._llmClientOptions)
        {
            if (options.Name.Equals(clientName, StringComparison.OrdinalIgnoreCase))
            {
                activity?.SetTag("agentic.models.client_type", options.ClientType);
                this._logger.LogInformation("Resolved LLM client {ClientName} ({ClientType}).", options.Name, options.ClientType);
                return CreateChatClient(options);
            }
        }

        activity?.SetStatus(ActivityStatusCode.Error, "Client configuration not found");
        this._logger.LogWarning("No LLM client configuration found with name {ClientName}.", clientName);
        throw new InvalidOperationException($"No LLM client configuration found with name '{clientName}'.");
    }

    private static (IChatClient Client, string ClientType) CreateChatClient(LlmClientOptions options)
    {
        return options.ClientType.ToUpperInvariant() switch
        {
            "CLAUDE" => (new ClaudeClient(options).CreateChatClient(), "Claude"),
            "OPENAI" => (new OpenAIClient(options).CreateChatClient(), "OpenAI"),
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
}
