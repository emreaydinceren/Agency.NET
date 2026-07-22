using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agency.Llm.OpenAI;

/// <summary>
/// Rewrites a failed <see cref="ClientResultException"/> into an <see cref="InvalidOperationException"/>
/// that includes the server's response body. <see cref="ClientResultException"/>'s own message (e.g.
/// "Service request failed. Status: 400 (Bad Request)") only includes the status code - the body is
/// dropped whenever the error shape doesn't match OpenAI's own <c>{"error": {"message": ...}}</c>
/// convention, which is exactly the case for LM Studio's <c>{"error": "..."}</c> string-typed errors
/// (e.g. context-length-exceeded).
/// </summary>
internal sealed class DescriptiveErrorChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ClientResultException ex)
        {
            throw Describe(ex);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }

                    update = enumerator.Current;
                }
                catch (ClientResultException ex)
                {
                    throw Describe(ex);
                }

                yield return update;
            }
        }
    }

    private static InvalidOperationException Describe(ClientResultException ex)
    {
        string body = ex.GetRawResponse()?.Content?.ToString() ?? "(no response body)";
        return new InvalidOperationException($"LLM request failed (HTTP {ex.Status}). Response body: {body}", ex);
    }
}
