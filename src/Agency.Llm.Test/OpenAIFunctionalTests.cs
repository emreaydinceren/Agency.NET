namespace Agency.Llm.Test;

/// <summary>
/// Functional tests for <see cref="Agency.Llm.OpenAI.OpenAIClient"/> using LM Studio.
/// Run with:  dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// Requires LM Studio running with qwen/qwen3-coder-next loaded at http://localhost:1234
/// </summary>
[Trait("Category", "Functional")]
public sealed class OpenAIFunctionalTests
{
    private const string Model = "qwen/qwen3-coder-next";

    private static readonly Agency.Llm.OpenAI.OpenAIClient Client = new(
        Microsoft.Extensions.Options.Options.Create(new Agency.Llm.OpenAI.OpenAIClientOptions
        {
            ApiKey = "lm-studio",
            BaseUrl = "http://localhost:1234/v1",
        }));

    [Fact]
    public async Task SendAsync_ReturnsWithoutError()
    {
        await Client.SendAsync(Model, "Reply with one word: hello");
    }

    [Fact]
    public async Task StreamAsync_YieldsAtLeastOneChunk()
    {
        var chunks = new List<string>();

        await foreach (var chunk in Client.StreamAsync(Model, "Reply with one word: hello"))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task StreamAsync_JoinedChunksFormNonEmptyResponse()
    {
        var sb = new System.Text.StringBuilder();

        await foreach (var chunk in Client.StreamAsync(Model, "What is 2 + 2? Reply with just the number."))
        {
            sb.Append(chunk);
        }

        Assert.False(string.IsNullOrWhiteSpace(sb.ToString()));
    }

    [Fact]
    public async Task StreamAsync_ChunksAreConsistentWithSendAsync()
    {
        const string prompt = "Reply with exactly: streaming works";

        var streamed = new System.Text.StringBuilder();
        await foreach (var chunk in Client.StreamAsync(Model, prompt))
        {
            streamed.Append(chunk);
        }

        await Client.SendAsync(Model, prompt);

        Assert.False(string.IsNullOrWhiteSpace(streamed.ToString()));
    }

    [Fact]
    public async Task SendAsync_WithTemperature_ReturnsWithoutError()
    {
        await Client.SendAsync(Model, "Reply with one word: hello", temperature: 0.7f);
    }

    [Fact]
    public async Task StreamAsync_WithTemperature_YieldsAtLeastOneChunk()
    {
        var chunks = new List<string>();

        await foreach (var chunk in Client.StreamAsync(Model, "Reply with one word: hello", temperature: 0.7f))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
    }
}
