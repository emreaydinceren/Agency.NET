using Agency.Memory.Common.Storage;

namespace Agency.Memory.Distiller.Test.Stubs;

/// <summary>In-memory stub for <see cref="IDeadLetterStore"/>.</summary>
internal sealed class FakeDeadLetterStore : IDeadLetterStore
{
    /// <summary>Gets all entries written to the dead-letter store.</summary>
    internal List<(string UserId, string? SessionId, string JobKind, object Payload, Exception Error)> Entries { get; } = [];

    /// <inheritdoc/>
    public Task WriteAsync(
        string userId,
        string? sessionId,
        string jobKind,
        object payload,
        Exception exception,
        CancellationToken ct = default)
    {
        this.Entries.Add((userId, sessionId, jobKind, payload, exception));
        return Task.CompletedTask;
    }
}
