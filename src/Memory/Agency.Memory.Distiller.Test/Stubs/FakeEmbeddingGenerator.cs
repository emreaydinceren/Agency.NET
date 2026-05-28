using Agency.Embeddings.Common;

namespace Agency.Memory.Distiller.Test.Stubs;

/// <summary>Stub embedding generator that returns a deterministic constant vector.</summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly float[] _vector;

    internal FakeEmbeddingGenerator(int dim = 4)
    {
        this._vector = Enumerable.Range(0, dim).Select(i => (float)(i + 1) / dim).ToArray();
    }

    /// <inheritdoc/>
    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<ReadOnlyMemory<float>>(this._vector);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> inputs, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
            inputs.Select(_ => (ReadOnlyMemory<float>)this._vector).ToList());
}
