
using Agency.Llm.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agency.Llm.OpenAI;
/// <summary>
/// Creates <see cref="IChatClient"/> instances backed by an OpenAI-compatible API.
/// Also implements <see cref="IModelProvider"/> so callers can enumerate available models.
/// </summary>
public sealed class OpenAIClient : IModelProvider
{
    private readonly LlmClientOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Creates a factory from configured options.</summary>
    public OpenAIClient(IOptions<LlmClientOptions> options, ILoggerFactory? loggerFactory = null)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value, loggerFactory)
    {
    }

    /// <summary>Creates a factory from configured options.</summary>
    public OpenAIClient(LlmClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this._options = options;
        this._loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Creates an <see cref="IChatClient"/> wired with OpenTelemetry and logging middleware.
    /// The model is selected per-request via <see cref="ChatOptions.ModelId"/>.
    /// </summary>
    public IChatClient CreateChatClient()
    {
        var underlying = BuildOpenAIClient(this._options, this._loggerFactory);

        // "default" is a placeholder; the actual model is selected per-request via ChatOptions.ModelId.
        var builder = underlying
            .GetChatClient("default")
            .AsIChatClient()
            .AsBuilder()
            .Use(inner => new DescriptiveErrorChatClient(inner))
            .UseOpenTelemetry()
            .UseLogging(this._loggerFactory ?? NullLoggerFactory.Instance);

        return builder.Build();
    }

    /// <summary>
    /// LM Studio's native model-catalogue endpoint. This is enrichment only (spec P2): the
    /// OpenAI-compatible <c>/v1/models</c> call above is the required path that every
    /// OpenAI-compatible server answers, and this richer, vendor-specific call is attempted
    /// afterward purely to fill in optional <see cref="Model"/> metadata. Any failure here —
    /// network error, non-success status, or malformed JSON — is swallowed and the plain
    /// <c>/v1/models</c> result is returned unchanged (P3: absent data stays absent).
    /// </summary>
    private const string LmStudioNativeModelsPath = "/api/v0/models";

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var client = BuildOpenAIClient(this._options, this._loggerFactory);
        var result = await client.GetOpenAIModelClient().GetModelsAsync(cancellationToken);
        var models = result.Value
            .Select(static m => new Model(m.Id, m.Id))
            .ToList();

        var enrichment = await TryFetchEnrichmentAsync(cancellationToken).ConfigureAwait(false);
        if (enrichment is null)
        {
            return models;
        }

        return models
            .Select(m => enrichment.TryGetValue(m.Id, out var e)
                ? m with { Kind = e.Kind, ContextLength = e.ContextLength, IsLoaded = e.IsLoaded }
                : m)
            .ToList();
    }

    /// <summary>
    /// Attempts to fetch the richer, vendor-specific model catalogue keyed by model id.
    /// Returns <see langword="null"/> on any failure — this call must never fail model
    /// enumeration (P2).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ModelEnrichment>?> TryFetchEnrichmentAsync(
        CancellationToken cancellationToken)
    {
        if (this._options.BaseUrl is null)
        {
            return null;
        }

        try
        {
            var nativeUri = new Uri(new Uri(this._options.BaseUrl), LmStudioNativeModelsPath);

            using var http = new HttpClient();
            if (!string.IsNullOrEmpty(this._options.ApiKey))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this._options.ApiKey);
            }

            using var response = await http.GetAsync(nativeUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<NativeModelCatalogue>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (payload?.Data is null)
            {
                return null;
            }

            var map = new Dictionary<string, ModelEnrichment>();
            foreach (var entry in payload.Data)
            {
                if (entry.Id is not null)
                {
                    map[entry.Id] = new ModelEnrichment(MapKind(entry.Type), entry.MaxContextLength, MapLoaded(entry.State));
                }
            }

            return map;
        }
        catch
        {
            return null;
        }
    }

    private static ModelKind? MapKind(string? type) => type switch
    {
        null => null,
        "llm" => ModelKind.Chat,
        "vlm" => ModelKind.Vision,
        "embeddings" => ModelKind.Embedding,
        _ => ModelKind.Unknown,
    };

    private static bool? MapLoaded(string? state) => state switch
    {
        "loaded" => true,
        "not-loaded" => false,
        _ => null,
    };

    private readonly record struct ModelEnrichment(ModelKind? Kind, int? ContextLength, bool? IsLoaded);

    private sealed class NativeModelCatalogue
    {
        [JsonPropertyName("data")]
        public List<NativeModelEntry>? Data { get; set; }
    }

    private sealed class NativeModelEntry
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("max_context_length")]
        public int? MaxContextLength { get; set; }
    }

    private static global::OpenAI.OpenAIClient BuildOpenAIClient(LlmClientOptions opts, ILoggerFactory? loggerFactory)
    {
        // ApiKeyCredential rejects an empty string with an ArgumentException naming only its own
        // 'key' parameter, which tells an operator nothing about which configuration entry is at
        // fault. Fail here instead, naming the exact key and its environment-variable form.
        // (The raw-HTTP path above deliberately tolerates an empty key by omitting the
        // Authorization header; this SDK path cannot, so it must at least say so clearly.)
        if (string.IsNullOrEmpty(opts.ApiKey))
        {
            throw new InvalidOperationException(
                $"ApiKey is not configured for LLM client '{opts.Name}'. Set "
                + $"Agent:LLmClients:<n>:ApiKey (environment: Agent__LLmClients__<n>__ApiKey) for the "
                + $"entry named '{opts.Name}'. A local OpenAI-compatible server ignores the value, so "
                + "any non-empty placeholder is sufficient.");
        }

        var credential = new ApiKeyCredential(opts.ApiKey);
        var clientOptions = new global::OpenAI.OpenAIClientOptions();

        if (opts.BaseUrl is not null)
        {
            clientOptions.Endpoint = new Uri(opts.BaseUrl);
        }

        if (opts.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            clientOptions.NetworkTimeout = timeout;
        }

        if (opts.MaxRetries is { } maxRetries && maxRetries >= 0)
        {
            clientOptions.RetryPolicy = new ClientRetryPolicy(maxRetries);
        }

        // SuppressThinking is the back-compat gate: when set, it must behave exactly as before
        // EnableThinking/ThinkingBudgetTokens existed — force enable_thinking:false and
        // thinking_budget_tokens:0 unconditionally, overriding either explicit setting.
        bool? enableThinking = opts.SuppressThinking ? false : opts.EnableThinking;
        int? thinkingBudgetTokens = opts.SuppressThinking ? 0 : opts.ThinkingBudgetTokens;

        if (enableThinking is not null || thinkingBudgetTokens is not null)
        {
            clientOptions.AddPolicy(new SuppressThinkingPipelinePolicy(enableThinking, thinkingBudgetTokens), PipelinePosition.PerCall);
        }

        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<OpenAIClient>();
        clientOptions.AddPolicy(new FailedRequestLoggingPipelinePolicy(logger), PipelinePosition.PerCall);

        return new global::OpenAI.OpenAIClient(credential, clientOptions);
    }
}
