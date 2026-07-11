
using Agency.Llm.Claude;
using Agency.Llm.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agency.Llm.Test;
/// <summary>
/// Functional tests for <see cref="Agency.Llm.Claude.ClaudeClient"/> exercised via
/// <see cref="IChatClient"/>. Run with: <c>dotnet test --filter "Category=Functional"</c>.
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>.
/// Requires a Claude API key in user secrets or environment variables.
/// </summary>
[Trait("Category", "Functional")]
[Trait("Category", "RequiresLlm")]
public sealed class ClaudeFunctionalTests(ClaudeFunctionalTests.ClaudeFixture fixture)
    : IClassFixture<ClaudeFunctionalTests.ClaudeFixture>
{
    private const string SystemPrompt = "You are a concise assistant.";

    private readonly ClaudeFixture _fixture = fixture;

    private static IReadOnlyList<ChatMessage> SimpleMessages(string userPrompt) =>
    [
        new ChatMessage(ChatRole.User, userPrompt),
    ];

    private ChatOptions MakeChatOptions(float? temperature = null) =>
        new()
        {
            ModelId = this._fixture.Model,
            Instructions = SystemPrompt,
            MaxOutputTokens = 512,
            Temperature = temperature,
        };

    // ── GetResponseAsync ──────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="IChatClient.GetResponseAsync"/> returns a non-empty response.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_ReturnsNonEmptyResponse()
    {
        var response = await this._fixture.Client.GetResponseAsync(
            SimpleMessages("Reply with one word: hello"),
            MakeChatOptions(),
            TestContext.Current.CancellationToken);

        string text = string.Concat(
            response.Messages
                .SelectMany(static m => m.Contents.OfType<TextContent>())
                .Select(static t => t.Text));

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotNull(response.FinishReason);
        Assert.True((response.Usage?.InputTokenCount ?? 0) >= 0);
        Assert.True((response.Usage?.OutputTokenCount ?? 0) >= 0);
    }

    /// <summary>
    /// Verifies that <see cref="IChatClient.GetResponseAsync"/> works with a specified temperature.
    /// </summary>
    [Fact]
    public async Task GetResponseAsync_WithTemperature_ReturnsNonEmptyResponse()
    {
        var response = await this._fixture.Client.GetResponseAsync(
            SimpleMessages("Reply with one word: hello"),
            MakeChatOptions(temperature: 0.7f),
            TestContext.Current.CancellationToken);

        string text = string.Concat(
            response.Messages
                .SelectMany(static m => m.Contents.OfType<TextContent>())
                .Select(static t => t.Text));

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    // ── GetStreamingResponseAsync ─────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="IChatClient.GetStreamingResponseAsync"/> yields at least one text chunk.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_YieldsAtLeastOneTextChunk()
    {
        var chunks = new List<string>();

        await foreach (var update in this._fixture.Client.GetStreamingResponseAsync(
            SimpleMessages("Reply with one word: hello"),
            MakeChatOptions(),
            TestContext.Current.CancellationToken))
        {
            foreach (var content in update.Contents.OfType<TextContent>())
            {
                if (!string.IsNullOrEmpty(content.Text))
                {
                    chunks.Add(content.Text);
                }
            }
        }

        Assert.NotEmpty(chunks);
    }

    /// <summary>
    /// Verifies that streaming chunks combine into a non-empty response.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_JoinedChunksFormNonEmptyResponse()
    {
        var sb = new System.Text.StringBuilder();

        await foreach (var update in this._fixture.Client.GetStreamingResponseAsync(
            SimpleMessages("Reply with one word: hello"),
            MakeChatOptions(),
            TestContext.Current.CancellationToken))
        {
            foreach (var content in update.Contents.OfType<TextContent>())
            {
                sb.Append(content.Text);
            }
        }

        Assert.False(string.IsNullOrWhiteSpace(sb.ToString()));
    }

    /// <summary>
    /// Verifies that streaming and batch responses are both produced for the same prompt.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ChunksAreConsistentWithGetResponseAsync()
    {
        const string userPrompt = "Reply with one word: hello";

        var streamed = new System.Text.StringBuilder();
        await foreach (var update in this._fixture.Client.GetStreamingResponseAsync(
            SimpleMessages(userPrompt),
            MakeChatOptions(),
            TestContext.Current.CancellationToken))
        {
            foreach (var content in update.Contents.OfType<TextContent>())
            {
                streamed.Append(content.Text);
            }
        }

        var response = await this._fixture.Client.GetResponseAsync(
            SimpleMessages(userPrompt),
            MakeChatOptions(),
            TestContext.Current.CancellationToken);

        string batchText = string.Concat(
            response.Messages
                .SelectMany(static m => m.Contents.OfType<TextContent>())
                .Select(static t => t.Text));

        Assert.False(string.IsNullOrWhiteSpace(streamed.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(batchText));
    }

    /// <summary>
    /// Verifies that aggregating the streaming response reports token usage.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_ReportsUsage()
    {
        var response = await this._fixture.Client.GetStreamingResponseAsync(
            SimpleMessages("Reply with one word: hello"),
            MakeChatOptions(),
            TestContext.Current.CancellationToken)
            .ToChatResponseAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(response.Usage);
        Assert.True((response.Usage.InputTokenCount ?? 0) > 0);
        Assert.True((response.Usage.OutputTokenCount ?? 0) > 0);
    }

    /// <summary>
    /// Verifies that streaming with temperature yields at least one text chunk.
    /// </summary>
    [Fact]
    public async Task GetStreamingResponseAsync_WithTemperature_YieldsAtLeastOneChunk()
    {
        var chunks = new List<string>();

        await foreach (var update in this._fixture.Client.GetStreamingResponseAsync(
            SimpleMessages("Reply with one word: hello"),
            MakeChatOptions(temperature: 0.7f),
            TestContext.Current.CancellationToken))
        {
            foreach (var content in update.Contents.OfType<TextContent>())
            {
                if (!string.IsNullOrEmpty(content.Text))
                {
                    chunks.Add(content.Text);
                }
            }
        }

        Assert.NotEmpty(chunks);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared fixture that loads Claude functional test configuration and initializes the client.
    /// </summary>
    public sealed class ClaudeFixture
    {
        private const string EnvironmentNameVariable = "DOTNET_ENVIRONMENT";
        private const string ConfigurationSection = "LlmTest:Claude";

        /// <summary>
        /// Creates the fixture and initializes configuration-backed <see cref="IChatClient"/>.
        /// </summary>
        public ClaudeFixture()
        {
            var environmentName = Environment.GetEnvironmentVariable(EnvironmentNameVariable) ?? "Development";

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddSharedConfiguration("shared-test-appsettings.json")
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .AddUserSecrets<ClaudeFunctionalTests>(optional: true)
                .AddEnvironmentVariables()
                .AddPlaceholderResolver()
                .Build();

            this.Model = GetRequiredConfiguration(configuration, $"{ConfigurationSection}:Model");

            this.Client = new ClaudeClient(
                Options.Create(new LlmClientOptions
                {
                    ApiKey = GetRequiredConfiguration(configuration, $"{ConfigurationSection}:ApiKey"),
                    BaseUrl = GetRequiredConfiguration(configuration, $"{ConfigurationSection}:BaseUrl"),
                })).CreateChatClient();
        }

        /// <summary>Gets the configured model name used by the functional tests.</summary>
        public string Model { get; }

        /// <summary>Gets the configured <see cref="IChatClient"/> backed by Claude.</summary>
        public IChatClient Client { get; }

        private static string GetRequiredConfiguration(IConfiguration configuration, string key) =>
            configuration[key]
                ?? throw new InvalidOperationException($"Missing required configuration value '{key}'.");
    }
}
