namespace Agency.Llm.Abstractions;

/// <summary>
/// Defines a common contract for Llm provider clients.
/// </summary>
public interface ILlmClient
{
    Task SendAsync(
        string model,
        string content,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> StreamAsync(
        string model,
        string content,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default);
}
