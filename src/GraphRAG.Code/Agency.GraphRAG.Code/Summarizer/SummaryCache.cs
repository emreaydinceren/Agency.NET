using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Caches generated summaries by chunk content hash and model tier in a SQLite-backed,
/// write-through store so that progress survives process crashes and restarts.
/// </summary>
public sealed class SummaryCache : IDisposable
{
    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS summary_cache (" +
        "    content_hash TEXT NOT NULL," +
        "    model_tier TEXT NOT NULL," +
        "    one_line TEXT NOT NULL," +
        "    detailed TEXT NOT NULL," +
        "    probable_callees TEXT NOT NULL," +
        "    PRIMARY KEY (content_hash, model_tier)" +
        ") WITHOUT ROWID;";

    private const string CreateEmbeddingTableSql =
        "CREATE TABLE IF NOT EXISTS embedding_cache (" +
        "    content_hash TEXT NOT NULL," +
        "    embedding_model_id TEXT NOT NULL," +
        "    embedding BLOB NOT NULL," +
        "    PRIMARY KEY (content_hash, embedding_model_id)" +
        ") WITHOUT ROWID;";

    private const string SelectEmbeddingSql =
        "SELECT embedding FROM embedding_cache " +
        "WHERE content_hash = $hash AND embedding_model_id = $modelId;";

    private const string UpsertEmbeddingSql =
        "INSERT INTO embedding_cache (content_hash, embedding_model_id, embedding) " +
        "VALUES ($hash, $modelId, $embedding) " +
        "ON CONFLICT(content_hash, embedding_model_id) DO UPDATE SET embedding = excluded.embedding;";

    private const string SelectSql =
        "SELECT one_line, detailed, probable_callees FROM summary_cache " +
        "WHERE content_hash = $hash AND model_tier = $tier;";

    private const string SelectAnySql =
        "SELECT one_line, detailed, probable_callees FROM summary_cache " +
        "WHERE content_hash = $hash " +
        "ORDER BY CASE model_tier WHEN 'Strong' THEN 0 WHEN 'Standard' THEN 1 WHEN 'Cheap' THEN 2 WHEN 'Cheapest' THEN 3 ELSE 4 END " +
        "LIMIT 1;";

    private const string UpsertSql =
        "INSERT INTO summary_cache (content_hash, model_tier, one_line, detailed, probable_callees) " +
        "VALUES ($hash, $tier, $oneLine, $detailed, $callees) " +
        "ON CONFLICT(content_hash, model_tier) DO UPDATE SET " +
        "    one_line = excluded.one_line," +
        "    detailed = excluded.detailed," +
        "    probable_callees = excluded.probable_callees;";

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    /// <summary>
    /// Initializes a new <see cref="SummaryCache"/> backed by a SQLite database at <paramref name="databasePath"/>.
    /// </summary>
    /// <param name="databasePath">
    /// Absolute path to the SQLite file. The parent directory must exist.
    /// Pass <c>":memory:"</c> for a non-persistent in-memory store (useful in tests).
    /// </param>
    public SummaryCache(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string connectionString = string.Equals(databasePath, ":memory:", StringComparison.Ordinal)
            ? "Data Source=:memory:"
            : $"Data Source={databasePath};Cache=Shared";

        this._connection = new SqliteConnection(connectionString);
        this._connection.Open();

        ExecuteNonQuery("PRAGMA journal_mode=WAL;");
        ExecuteNonQuery("PRAGMA synchronous=NORMAL;");
        ExecuteNonQuery(CreateTableSql);
        ExecuteNonQuery(CreateEmbeddingTableSql);
    }

    /// <summary>
    /// Attempts to read a cached summary.
    /// </summary>
    /// <param name="chunkContentHash">The chunk content hash.</param>
    /// <param name="modelTier">The model tier used to generate the summary.</param>
    /// <param name="entry">The cached summary when present.</param>
    /// <returns><see langword="true"/> when a matching entry exists; otherwise <see langword="false"/>.</returns>
    public bool TryGet(string chunkContentHash, string modelTier, [NotNullWhen(true)] out SummaryCacheEntry? entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelTier);

        lock (this._gate)
        {
            using SqliteCommand command = this._connection.CreateCommand();
            command.CommandText = SelectSql;
            command.Parameters.AddWithValue("$hash", chunkContentHash);
            command.Parameters.AddWithValue("$tier", modelTier);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                entry = null;
                return false;
            }

            string oneLine = reader.GetString(0);
            string detailed = reader.GetString(1);
            string calleesJson = reader.GetString(2);
            IReadOnlyList<string> callees = DeserializeCallees(calleesJson);

            entry = new SummaryCacheEntry(oneLine, detailed, callees);
            return true;
        }
    }

    /// <summary>
    /// Attempts to read a cached summary for any model tier, preferring higher-quality tiers.
    /// Used as a fallback when the exact-tier lookup misses — for example when a symbol's
    /// <c>isLeaf</c> status changes between a full walk and an incremental walk.
    /// </summary>
    /// <param name="chunkContentHash">The chunk content hash.</param>
    /// <param name="entry">The cached summary when present.</param>
    /// <returns><see langword="true"/> when any matching entry exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetAny(string chunkContentHash, [NotNullWhen(true)] out SummaryCacheEntry? entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkContentHash);

        lock (this._gate)
        {
            using SqliteCommand command = this._connection.CreateCommand();
            command.CommandText = SelectAnySql;
            command.Parameters.AddWithValue("$hash", chunkContentHash);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                entry = null;
                return false;
            }

            string oneLine = reader.GetString(0);
            string detailed = reader.GetString(1);
            string calleesJson = reader.GetString(2);
            IReadOnlyList<string> callees = DeserializeCallees(calleesJson);

            entry = new SummaryCacheEntry(oneLine, detailed, callees);
            return true;
        }
    }

    /// <summary>
    /// Stores or replaces a cached summary, persisting it to disk before returning.
    /// </summary>
    /// <param name="chunkContentHash">The chunk content hash.</param>
    /// <param name="modelTier">The model tier used to generate the summary.</param>
    /// <param name="entry">The summary to cache.</param>
    public void Set(string chunkContentHash, string modelTier, SummaryCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelTier);
        ArgumentNullException.ThrowIfNull(entry);

        string calleesJson = JsonSerializer.Serialize(entry.ProbableCallees);

        lock (this._gate)
        {
            using SqliteCommand command = this._connection.CreateCommand();
            command.CommandText = UpsertSql;
            command.Parameters.AddWithValue("$hash", chunkContentHash);
            command.Parameters.AddWithValue("$tier", modelTier);
            command.Parameters.AddWithValue("$oneLine", entry.OneLine);
            command.Parameters.AddWithValue("$detailed", entry.Detailed);
            command.Parameters.AddWithValue("$callees", calleesJson);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Attempts to read a cached embedding.
    /// </summary>
    /// <param name="contentHash">The chunk content hash.</param>
    /// <param name="embeddingModelId">The embedding model identifier.</param>
    /// <param name="embedding">The cached embedding when present.</param>
    /// <returns><see langword="true"/> when a matching entry exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetEmbedding(string contentHash, string embeddingModelId, out ReadOnlyMemory<float> embedding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);

        lock (this._gate)
        {
            using SqliteCommand command = this._connection.CreateCommand();
            command.CommandText = SelectEmbeddingSql;
            command.Parameters.AddWithValue("$hash", contentHash);
            command.Parameters.AddWithValue("$modelId", embeddingModelId);

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                embedding = default;
                return false;
            }

            embedding = DeserializeEmbedding((byte[])reader.GetValue(0));
            return true;
        }
    }

    /// <summary>
    /// Stores or replaces a cached embedding, persisting it to disk before returning.
    /// </summary>
    /// <param name="contentHash">The chunk content hash.</param>
    /// <param name="embeddingModelId">The embedding model identifier.</param>
    /// <param name="embedding">The embedding to cache.</param>
    public void SetEmbedding(string contentHash, string embeddingModelId, ReadOnlyMemory<float> embedding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);

        byte[] bytes = SerializeEmbedding(embedding);

        lock (this._gate)
        {
            using SqliteCommand command = this._connection.CreateCommand();
            command.CommandText = UpsertEmbeddingSql;
            command.Parameters.AddWithValue("$hash", contentHash);
            command.Parameters.AddWithValue("$modelId", embeddingModelId);
            command.Parameters.AddWithValue("$embedding", bytes);
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this._connection.Dispose();
    }

    private void ExecuteNonQuery(string sql)
    {
        using SqliteCommand command = this._connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static byte[] SerializeEmbedding(ReadOnlyMemory<float> embedding) =>
        MemoryMarshal.AsBytes(embedding.Span).ToArray();

    private static ReadOnlyMemory<float> DeserializeEmbedding(byte[] bytes)
    {
        float[] floats = new float[bytes.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(floats);
        return new ReadOnlyMemory<float>(floats);
    }

    private static IReadOnlyList<string> DeserializeCallees(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        string[]? parsed = JsonSerializer.Deserialize<string[]>(json);
        return parsed ?? [];
    }
}

/// <summary>
/// Represents a cached summarization result.
/// </summary>
public sealed record SummaryCacheEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryCacheEntry"/> class.
    /// </summary>
    /// <param name="oneLine">The one-line summary.</param>
    /// <param name="detailed">The detailed summary.</param>
    /// <param name="probableCallees">The probable callees extracted from the summary.</param>
    /// <param name="embedding">The embedding generated from <paramref name="oneLine"/>, or empty if not yet generated.</param>
    public SummaryCacheEntry(string oneLine, string detailed, IReadOnlyList<string> probableCallees, ReadOnlyMemory<float> embedding = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oneLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailed);
        ArgumentNullException.ThrowIfNull(probableCallees);

        this.OneLine = oneLine;
        this.Detailed = detailed;
        this.ProbableCallees = probableCallees.ToArray();
        this.Embedding = embedding;
    }

    /// <summary>
    /// Gets the one-line summary.
    /// </summary>
    public string OneLine { get; }

    /// <summary>
    /// Gets the detailed summary.
    /// </summary>
    public string Detailed { get; }

    /// <summary>
    /// Gets the probable callees.
    /// </summary>
    public IReadOnlyList<string> ProbableCallees { get; }

    /// <summary>
    /// Gets the embedding generated from <see cref="OneLine"/>, or empty if not yet cached.
    /// </summary>
    public ReadOnlyMemory<float> Embedding { get; }
}
