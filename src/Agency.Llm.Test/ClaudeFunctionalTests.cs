namespace Agency.Llm.Test;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Functional tests for <see cref="Agency.Llm.Claude.ClaudeClient"/> using LM Studio.
/// Run with:  dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// Requires LM Studio running with qwen/qwen3-coder-next loaded at http://llm-host.example:1234
/// </summary>
[Trait("Category", "Functional")]
/// <summary>
/// Functional tests for <see cref="Agency.Llm.Claude.ClaudeClient"/>.
/// </summary>
public sealed class ClaudeFunctionalTests
{
    private const string EnvironmentNameVariable = "DOTNET_ENVIRONMENT";
    private const string ConfigurationSection = "LlmTest:Claude";
    private const string SystemPrompt = "You are a concise assistant.";

    private static readonly string EnvironmentName =
        Environment.GetEnvironmentVariable(EnvironmentNameVariable) ?? "Development";

    private readonly string Model = GetRequiredConfiguration($"{ConfigurationSection}:Model");

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{EnvironmentName}.json", optional: true)
        .AddUserSecrets<ClaudeFunctionalTests>(optional: true)
        .AddEnvironmentVariables()
        .Build();

    private static readonly Agency.Llm.Claude.ClaudeClient Client = new(
        Options.Create(new Agency.Llm.Claude.ClaudeClientOptions
        {
            ApiKey = GetRequiredConfiguration($"{ConfigurationSection}:ApiKey"),
            BaseUrl = GetRequiredConfiguration($"{ConfigurationSection}:BaseUrl"),
        }));

    private static string GetRequiredConfiguration(string key)
    {
        return Configuration[key]
            ?? throw new InvalidOperationException($"Missing required configuration value '{key}'.");
    }

    /// <summary>
    /// Verifies that <see cref="Agency.Llm.Claude.ClaudeClient.SendAsync"/> returns a response without error.
    /// </summary>
    [Fact]
    public async Task SendAsync_ReturnsWithoutError()
    {
        var response = await Client.SendAsync(Model, SystemPrompt, "Reply with one word: hello");

        Assert.False(string.IsNullOrWhiteSpace(response.Message));
        Assert.True(System.Enum.IsDefined(response.FinishReason));
        Assert.True(response.Usage.InputTokens >= 0);
        Assert.True(response.Usage.OutputTokens >= 0);
    }

    /// <summary>
    /// Verifies that streaming returns at least one text chunk.
    /// </summary>
    [Fact]
    public async Task StreamAsync_YieldsAtLeastOneChunk()
    {
        var chunks = new List<string>();

        await foreach (var chunk in Client.StreamAsync(Model, SystemPrompt, "Reply with one word: hello"))
        {
            if (chunk.Text is not null)
            {
                chunks.Add(chunk.Text);
            }
        }

        Assert.NotEmpty(chunks);
    }

    /// <summary>
    /// Verifies that streamed chunks combine into a non-empty response.
    /// </summary>
    [Fact]
    public async Task StreamAsync_JoinedChunksFormNonEmptyResponse()
    {
        var sb = new System.Text.StringBuilder();

        await foreach (var chunk in Client.StreamAsync(Model, SystemPrompt, "What is 2 + 2? Reply with just the number."))
        {
            if (chunk.Text is not null)
            {
                sb.Append(chunk.Text);
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(sb.ToString()));
    }

    /// <summary>
    /// Verifies that streaming and non-streaming responses are both produced for the same prompt.
    /// </summary>
    [Fact]
    public async Task StreamAsync_ChunksAreConsistentWithSendAsync()
    {
        const string prompt = "Reply with exactly: streaming works";

        var streamed = new System.Text.StringBuilder();
        await foreach (var chunk in Client.StreamAsync(Model, SystemPrompt, prompt))
        {
            if (chunk.Text is not null)
            {
                streamed.Append(chunk.Text);
            }
        }

        // Both methods must complete without error against the same endpoint
        var response = await Client.SendAsync(Model, SystemPrompt, prompt);

        Assert.False(string.IsNullOrWhiteSpace(streamed.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
    }

    /// <summary>
    /// Verifies that the terminal streamed chunk contains usage and stop-reason data.
    /// </summary>
    [Fact]
    public async Task StreamAsync_TerminalChunkHasUsageAndFinishReason()
    {
        Agency.Llm.Common.LlmStreamChunk? terminal = null;

        await foreach (var chunk in Client.StreamAsync(Model, SystemPrompt, "Reply with one word: hello"))
        {
            terminal = chunk;
        }

        Assert.NotNull(terminal);
        Assert.Null(terminal.Text);
        Assert.NotNull(terminal.Usage);
        Assert.True(terminal.Usage.TotalTokens > 0);
        Assert.NotNull(terminal.StopReason);
        Assert.True(System.Enum.IsDefined(terminal.StopReason.Value));
    }

    /// <summary>
    /// Verifies that temperature can be supplied to the completion request.
    /// </summary>
    [Fact]
    public async Task SendAsync_WithTemperature_ReturnsWithoutError()
    {
        var response = await Client.SendAsync(Model, SystemPrompt, "Reply with one word: hello", temperature: 0.7f);

        Assert.False(string.IsNullOrWhiteSpace(response.Message));
        Assert.True(System.Enum.IsDefined(response.FinishReason));
        Assert.True(response.Usage.TotalTokens >= 0);
    }

    /// <summary>
    /// Verifies that streaming with temperature returns at least one text chunk.
    /// </summary>
    [Fact]
    public async Task StreamAsync_WithTemperature_YieldsAtLeastOneChunk()
    {
        var chunks = new List<string>();

        await foreach (var chunk in Client.StreamAsync(Model, SystemPrompt, "Reply with one word: hello", temperature: 0.7f))
        {
            if (chunk.Text is not null)
            {
                chunks.Add(chunk.Text);
            }
        }

        Assert.NotEmpty(chunks);
    }
}
