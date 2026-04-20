namespace Agency.Llm.OpenAI;

using Agency.Llm.Common;
using global::OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.ClientModel;

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
        : this(options.Value, loggerFactory)
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
        var underlying = BuildOpenAIClient(this._options);

        // "default" is a placeholder; the actual model is selected per-request via ChatOptions.ModelId.
        var builder = underlying
            .GetChatClient("default")
            .AsIChatClient()
            .AsBuilder()
            .UseOpenTelemetry()
            .UseLogging(this._loggerFactory ?? NullLoggerFactory.Instance);

        return builder.Build();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var client = BuildOpenAIClient(this._options);
        var result = await client.GetOpenAIModelClient().GetModelsAsync(cancellationToken);
        return result.Value
            .Select(static m => new Model(m.Id, m.Id))
            .ToList();
    }

    private static global::OpenAI.OpenAIClient BuildOpenAIClient(LlmClientOptions opts)
    {
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

        return new global::OpenAI.OpenAIClient(credential, clientOptions);
    }
}
