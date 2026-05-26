
using Agency.Llm.Common;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Llm.Claude;
/// <summary>
/// Creates <see cref="IChatClient"/> instances backed by the Anthropic Claude API.
/// Also implements <see cref="IModelProvider"/> so callers can enumerate available models.
/// </summary>
public sealed class ClaudeClient : IModelProvider
{
    private readonly LlmClientOptions _options;
    private readonly ILoggerFactory? _loggerFactory;

    /// <summary>Creates a factory from configured options.</summary>
    public ClaudeClient(IOptions<LlmClientOptions> options, ILoggerFactory? loggerFactory = null)
        : this(options.Value, loggerFactory)
    {
    }

    /// <summary>Creates a factory from configured options.</summary>
    public ClaudeClient(LlmClientOptions options, ILoggerFactory? loggerFactory = null)
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
        var co = BuildClientOptions(this._options);
        var anthropic = new AnthropicClient(co);

        var builder = anthropic
            .AsIChatClient(string.Empty)
            .AsBuilder()
            .UseOpenTelemetry()
            .UseLogging(this._loggerFactory ?? NullLoggerFactory.Instance);

        return builder.Build();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var anthropic = new AnthropicClient(BuildClientOptions(this._options));
        var page = await anthropic.Models.List(cancellationToken: cancellationToken);
        var models = new List<Model>();

        foreach (var info in page.Items)
        {
            string displayName;
            try
            {
                displayName = info.DisplayName ?? info.ID;
            }
            catch (Anthropic.Exceptions.AnthropicInvalidDataException)
            {
                displayName = info.ID;
            }

            models.Add(new Model(info.ID, displayName));
        }

        return models;
    }

    private static ClientOptions BuildClientOptions(LlmClientOptions opts)
    {
        var co = new ClientOptions
        {
            ApiKey = opts.ApiKey,
            MaxRetries = opts.MaxRetries ?? ClientOptions.DefaultMaxRetries,
            Timeout = opts.Timeout ?? ClientOptions.DefaultTimeout,
        };

        if (opts.BaseUrl is not null)
        {
            co.BaseUrl = opts.BaseUrl;
        }

        return co;
    }
}
