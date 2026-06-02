using Microsoft.Extensions.AI;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Wraps an <see cref="IChatClient"/> as the Distiller's <see cref="ILlmClientAdapter"/>,
/// sending a single-turn prompt and returning the concatenated text response.
/// </summary>
/// <remarks>
/// Kept <c>internal sealed</c> so the Distiller controls the implementation. Test projects
/// access it via <c>[assembly: InternalsVisibleTo]</c> declared in
/// <c>Agency.Memory.Distiller/AssemblyInfo.cs</c>.
/// </remarks>
internal sealed class ChatClientLlmAdapter : ILlmClientAdapter
{
    private readonly IChatClient _client;
    private readonly string _model;

    /// <summary>
    /// Initialises a new <see cref="ChatClientLlmAdapter"/>.
    /// </summary>
    /// <param name="client">The underlying chat client.</param>
    /// <param name="model">The model identifier sent with each request.</param>
    internal ChatClientLlmAdapter(IChatClient client, string model)
    {
        this._client = client ?? throw new ArgumentNullException(nameof(client));
        this._model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <inheritdoc/>
    public async Task<string> SendAsync(string prompt, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, prompt),
        };

        var opts = new ChatOptions { ModelId = this._model, MaxOutputTokens = 2048 };
        ChatResponse response = await this._client.GetResponseAsync(messages, opts, ct)
            .ConfigureAwait(false);

        return string.Concat(
            response.Messages
                .SelectMany(static m => m.Contents.OfType<TextContent>())
                .Select(static t => t.Text));
    }
}
