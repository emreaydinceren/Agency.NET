using Agency.Memory.Distiller.Services;

namespace Agency.Memory.Distiller.Test.Stubs;

/// <summary>Stub LLM adapter that returns a canned JSON response for each call.</summary>
internal sealed class FakeLlmClientAdapter : ILlmClientAdapter
{
    private readonly Queue<string> _responses;
    private readonly List<Exception> _exceptions;
    private int _callIndex;

    /// <summary>Gets the prompts sent to this adapter.</summary>
    internal List<string> SentPrompts { get; } = [];

    /// <summary>Gets the total number of calls made.</summary>
    internal int CallCount => this._callIndex;

    internal FakeLlmClientAdapter(params string[] responses)
    {
        this._responses = new Queue<string>(responses);
        this._exceptions = [];
    }

    /// <summary>Queues an exception to be thrown on the next call.</summary>
    internal void QueueException(Exception ex) => this._exceptions.Add(ex);

    /// <inheritdoc/>
    public Task<string> SendAsync(string prompt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        this._callIndex++;
        this.SentPrompts.Add(prompt);

        // Check queued exceptions first.
        if (this._exceptions.Count > 0 && this._callIndex <= this._exceptions.Count)
        {
            throw this._exceptions[this._callIndex - 1];
        }

        if (this._responses.TryDequeue(out string? response))
        {
            return Task.FromResult(response);
        }

        return Task.FromResult("""{"records":[]}""");
    }
}
