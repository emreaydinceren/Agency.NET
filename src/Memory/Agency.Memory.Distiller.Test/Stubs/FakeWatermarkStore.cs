using System.Collections.Concurrent;
using Agency.Memory.Distiller.Services;

namespace Agency.Memory.Distiller.Test.Stubs;

/// <summary>In-memory stub for <see cref="IWatermarkStore"/>.</summary>
internal sealed class FakeWatermarkStore : IWatermarkStore
{
    private readonly ConcurrentDictionary<string, int> _watermarks = new();

    /// <inheritdoc/>
    public Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        string key = $"{userId}:{sessionId}";
        this._watermarks.TryGetValue(key, out int value);
        return Task.FromResult(value);
    }

    /// <inheritdoc/>
    public Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default)
    {
        string key = $"{userId}:{sessionId}";
        int effective = this._watermarks.AddOrUpdate(key, candidate, (_, existing) => Math.Max(existing, candidate));
        return Task.FromResult(effective);
    }

    /// <summary>Gets the current watermark value for the given session (test helper).</summary>
    internal int Get(string userId, string sessionId)
    {
        this._watermarks.TryGetValue($"{userId}:{sessionId}", out int v);
        return v;
    }
}
