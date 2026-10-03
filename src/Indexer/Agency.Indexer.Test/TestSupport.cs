using Agency.Embeddings.Common;
using Agency.Embeddings.OpenAI;

namespace Agency.Indexer.Test;

/// <summary>
/// Deterministic bag-of-words embedding: each lower-cased word increments one of 64 buckets, so texts sharing
/// words are close in cosine distance. Counts batch calls so tests can assert on embedding traffic.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    /// <summary>The embedding width.</summary>
    public const int Dimensions = 64;

    /// <summary>Every input passed to <see cref="GenerateEmbeddingsAsync"/>, in call order.</summary>
    public List<string> EmbeddedInputs { get; } = [];

    /// <inheritdoc/>
    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default) =>
        Task.FromResult(Embed(input));

    /// <inheritdoc/>
    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
    {
        List<string> list = inputs.ToList();
        this.EmbeddedInputs.AddRange(list);
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(list.Select(Embed).ToList());
    }

    private static ReadOnlyMemory<float> Embed(string text)
    {
        float[] vector = new float[Dimensions];
        foreach (string word in text.ToLowerInvariant().Split([' ', '\n', '\r', '\t', '"', '.', ',', '?', '#'], StringSplitOptions.RemoveEmptyEntries))
        {
            int bucket = 0;
            foreach (char c in word)
            {
                bucket = ((bucket * 31) + c) % Dimensions;
            }

            vector[bucket] += 1;
        }

        vector[0] += 0.01f;
        return vector;
    }
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates a fresh, empty directory under the system temp folder.</summary>
    public TempDirectory()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agency-indexer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }

    /// <summary>Gets the full path of the directory.</summary>
    public string Path { get; }

    /// <summary>Writes <paramref name="content"/> to <paramref name="relativePath"/>, creating folders as needed.</summary>
    /// <returns>The full path written.</returns>
    public string Write(string relativePath, string content)
    {
        string full = System.IO.Path.Combine(this.Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(this.Path, recursive: true);
    }
}

/// <summary>Builds an <see cref="IndexService"/> over a SQLite database through the production composition root.</summary>
internal static class Services
{
    /// <summary>Creates a service whose database lives at <paramref name="databasePath"/>.</summary>
    public static Task<IndexService> SqliteAsync(string databasePath, FakeEmbeddingGenerator embeddings, string model = "fake-model") =>
        Program.CreateServiceAsync(
            new IndexerSettings(
                StorageProvider.Sqlite,
                databasePath,
                new EmbeddingOptions { ModelId = model, Dimensions = FakeEmbeddingGenerator.Dimensions },
                ChunkSize: 64,
                ChunkOverlap: 0),
            embeddings,
            TestContext.Current.CancellationToken);
}
