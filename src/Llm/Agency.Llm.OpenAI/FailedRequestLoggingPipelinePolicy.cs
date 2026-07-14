using System.ClientModel.Primitives;
using Microsoft.Extensions.Logging;

namespace Agency.Llm.OpenAI;

/// <summary>
/// Logs the request URL whenever a chat-completion HTTP call returns a non-success status.
/// <see cref="System.ClientModel.ClientResultException"/> surfaces only the status code, not the
/// URL that was actually called - without this, a 404 from a misconfigured base URL is
/// indistinguishable in the log from a 404 caused by an unknown model or a server-side issue.
/// </summary>
internal sealed partial class FailedRequestLoggingPipelinePolicy(ILogger logger) : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        this.LogIfFailed(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        this.LogIfFailed(message);
    }

    private void LogIfFailed(PipelineMessage message)
    {
        if (message.Response is { } response && response.Status is < 200 or >= 300)
        {
            this.LogRequestFailed(message.Request.Uri, response.Status, response.ReasonPhrase ?? string.Empty);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "LLM HTTP request failed. Url={Url}, Status={Status} {ReasonPhrase}")]
    private partial void LogRequestFailed(Uri? url, int status, string reasonPhrase);
}
