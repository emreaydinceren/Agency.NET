namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Minimal abstraction over an LLM chat-completion call needed by the Distiller.
/// </summary>
/// <remarks>
/// This thin wrapper decouples the Distiller from a specific LLM client implementation
/// and makes the distiller testable with stub implementations that return canned JSON.
/// </remarks>
internal interface ILlmClientAdapter
{
    /// <summary>
    /// Sends <paramref name="prompt"/> as a single-turn request and returns the LLM's
    /// text response.
    /// </summary>
    /// <param name="prompt">The full prompt string (system + user combined).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The LLM's text response.</returns>
    Task<string> SendAsync(string prompt, CancellationToken ct = default);
}
