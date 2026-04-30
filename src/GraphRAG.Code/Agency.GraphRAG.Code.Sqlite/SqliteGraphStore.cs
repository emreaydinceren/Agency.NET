using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Sqlite.Migrations;
using Agency.GraphRAG.Code.Storage;
using Agency.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Sqlite;

/// <summary>
/// Persists GraphRAG code graph data in SQLite.
/// </summary>
public sealed class SqliteGraphStore : IGraphStore
{
    /// <summary>The activity source name used for graph store telemetry.</summary>
    public const string ActivitySourceName = "Agency.GraphRAG.Code.Sqlite";

    /// <summary>The meter name used for graph store telemetry.</summary>
    public const string MeterName = "Agency.GraphRAG.Code.Sqlite";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);
    private static readonly Counter<long> _operationCount =
        _meter.CreateCounter<long>("graphrag.sqlite.operations", unit: "{operation}", description: "Total number of graph store operations executed.");
    private static readonly Histogram<double> _operationDuration =
        _meter.CreateHistogram<double>("graphrag.sqlite.duration", unit: "ms", description: "Duration of graph store operations in milliseconds.");

    private readonly SqliteRunner _sqliteRunner;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger<SqliteGraphStore> _logger;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteGraphStore"/> class.
    /// </summary>
    /// <param name="sqliteRunner">The SQLite runner used for database operations.</param>
    /// <param name="embeddingGenerator">The embedding generator used for symbol embeddings.</param>
    /// <param name="logger">Optional logger.</param>
    public SqliteGraphStore(
        SqliteRunner sqliteRunner,
        IEmbeddingGenerator embeddingGenerator,
        ILogger<SqliteGraphStore>? logger = null)
    {
        this._sqliteRunner = sqliteRunner ?? throw new ArgumentNullException(nameof(sqliteRunner));
        this._embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this._logger = logger ?? NullLogger<SqliteGraphStore>.Instance;
        this._connectionString = GetConnectionString(sqliteRunner);
    }

    /// <inheritdoc />
    public Task InitializeSchemaAsync(CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "initialize",
            async activity =>
            {
                var migrationRunner = new SqliteMigrationRunner(this._connectionString);
                await migrationRunner.MigrateToLatestAsync(cancellationToken: cancellationToken);
                activity?.SetTag("graphrag.initialized", true);
            });

    /// <inheritdoc />
    public Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        return this.RunOperationAsync(
            "upsert-repo",
            activity =>
            {
                activity?.SetTag("graphrag.repo.id", repo.Id);

                return this._sqliteRunner.ExecuteAsync(
                    """
                    INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
                    VALUES (@id, @remoteUrl, @rootPath, @indexedCommit, @indexedAt, @isShallow)
                    ON CONFLICT (id) DO UPDATE
                    SET remote_url = excluded.remote_url,
                        root_path = excluded.root_path,
                        indexed_commit = excluded.indexed_commit,
                        indexed_at = excluded.indexed_at,
                        is_shallow = excluded.is_shallow;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = ToDbGuid(repo.Id),
                        ["remoteUrl"] = repo.RemoteUrl,
                        ["rootPath"] = repo.LocalPath,
                        ["indexedCommit"] = repo.IndexedCommit,
                        ["indexedAt"] = ToDbDateTime(repo.IndexedAt),
                        ["isShallow"] = repo.IsShallow ? 1 : 0,
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        return this.RunOperationAsync(
            "upsert-project",
            activity =>
            {
                activity?.SetTag("graphrag.project.id", project.Id);
                activity?.SetTag("graphrag.repo.id", project.RepoId);

                return this._sqliteRunner.ExecuteAsync(
                    """
                    INSERT INTO projects (id, repo_id, name, relative_path, manifest_path, language, ecosystem)
                    VALUES (@id, @repoId, @name, @relativePath, @manifestPath, @language, @ecosystem)
                    ON CONFLICT (id) DO UPDATE
                    SET repo_id = excluded.repo_id,
                        name = excluded.name,
                        relative_path = excluded.relative_path,
                        manifest_path = excluded.manifest_path,
                        language = excluded.language,
                        ecosystem = excluded.ecosystem;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = ToDbGuid(project.Id),
                        ["repoId"] = ToDbGuid(project.RepoId),
                        ["name"] = project.Name,
                        ["relativePath"] = project.RelativePath,
                        ["manifestPath"] = project.ManifestPath,
                        ["language"] = project.Language,
                        ["ecosystem"] = InferEcosystem(project.ManifestPath),
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);

        return this.RunOperationAsync(
            "upsert-external-packages",
            async activity =>
            {
                activity?.SetTag("graphrag.package.count", packages.Count);

                if (packages.Count == 0)
                {
                    return;
                }

                const string sqlPrefix = "BEGIN IMMEDIATE;";
                const string sqlSuffix = "COMMIT;";
                const string upsertSql =
                    """
                    INSERT INTO external_packages (id, project_id, name, version, version_resolved, ecosystem, scope)
                    VALUES (__ID__, __PROJECT_ID__, __NAME__, __VERSION__, __VERSION_RESOLVED__, __ECOSYSTEM__, __SCOPE__)
                    ON CONFLICT (id) DO UPDATE
                    SET project_id = excluded.project_id,
                        name = excluded.name,
                        version = excluded.version,
                        version_resolved = excluded.version_resolved,
                        ecosystem = excluded.ecosystem,
                        scope = excluded.scope;
                    """;

                var sqlBuilder = new StringBuilder(sqlPrefix);
                var parameters = new Dictionary<string, object?>(packages.Count * 7);

                for (int index = 0; index < packages.Count; index++)
                {
                    ExternalPackage package = packages[index];
                    string suffix = index.ToString(CultureInfo.InvariantCulture);
                    string statement = upsertSql
                        .Replace("__ID__", "@id" + suffix, StringComparison.Ordinal)
                        .Replace("__PROJECT_ID__", "@projectId" + suffix, StringComparison.Ordinal)
                        .Replace("__NAME__", "@name" + suffix, StringComparison.Ordinal)
                        .Replace("__VERSION__", "@version" + suffix, StringComparison.Ordinal)
                        .Replace("__VERSION_RESOLVED__", "@versionResolved" + suffix, StringComparison.Ordinal)
                        .Replace("__ECOSYSTEM__", "@ecosystem" + suffix, StringComparison.Ordinal)
                        .Replace("__SCOPE__", "@scope" + suffix, StringComparison.Ordinal);
                    _ = sqlBuilder.AppendLine(statement);

                    parameters["id" + suffix] = ToDbGuid(package.Id);
                    parameters["projectId" + suffix] = ToDbGuid(package.ProjectId);
                    parameters["name" + suffix] = package.Name;
                    parameters["version" + suffix] = package.Version;
                    parameters["versionResolved" + suffix] = package.Version;
                    parameters["ecosystem" + suffix] = package.Ecosystem;
                    parameters["scope" + suffix] = package.Scope;
                }

                _ = sqlBuilder.AppendLine(sqlSuffix);
                await this._sqliteRunner.ExecuteAsync(sqlBuilder.ToString(), parameters, cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        return this.RunOperationAsync(
            "upsert-file",
            activity =>
            {
                activity?.SetTag("graphrag.file.id", file.Id);
                activity?.SetTag("graphrag.project.id", file.ProjectId);

                return this._sqliteRunner.ExecuteAsync(
                    """
                    INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
                    VALUES (@id, @repoId, @projectId, @path, @language, @contentHash, @lastIndexedAt)
                    ON CONFLICT (id) DO UPDATE
                    SET repo_id = excluded.repo_id,
                        project_id = excluded.project_id,
                        path = excluded.path,
                        language = excluded.language,
                        content_hash = excluded.content_hash,
                        last_indexed_at = excluded.last_indexed_at;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = ToDbGuid(file.Id),
                        ["repoId"] = ToDbGuid(file.RepoId),
                        ["projectId"] = ToDbGuid(file.ProjectId),
                        ["path"] = file.Path,
                        ["language"] = file.Language,
                        ["contentHash"] = file.ContentHash,
                        ["lastIndexedAt"] = ToDbDateTime(DateTimeOffset.UtcNow),
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertModuleAsync(Agency.GraphRAG.Code.Domain.Module module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);

        return this.RunOperationAsync(
            "upsert-module",
            activity =>
            {
                activity?.SetTag("graphrag.module.id", module.Id);
                activity?.SetTag("graphrag.file.id", module.FileId);

                return this._sqliteRunner.ExecuteAsync(
                    """
                    INSERT INTO modules (id, file_id, project_id, name, path, kind)
                    VALUES (
                        @id,
                        @fileId,
                        (SELECT project_id FROM files WHERE id = @fileId),
                        @name,
                        NULL,
                        @kind)
                    ON CONFLICT (id) DO UPDATE
                    SET file_id = excluded.file_id,
                        project_id = (SELECT project_id FROM files WHERE id = excluded.file_id),
                        name = excluded.name,
                        path = excluded.path,
                        kind = excluded.kind;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = ToDbGuid(module.Id),
                        ["fileId"] = ToDbGuid(module.FileId),
                        ["name"] = module.Name,
                        ["kind"] = module.Kind,
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        return this.RunOperationAsync(
            "upsert-symbol",
            async activity =>
            {
                activity?.SetTag("graphrag.symbol.id", symbol.Id);
                await this.UpsertSymbolCoreAsync(symbol, cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        return this.RunOperationAsync(
            "upsert-symbol-batch",
            async activity =>
            {
                activity?.SetTag("graphrag.symbol.count", symbols.Count);

                if (symbols.Count == 0)
                {
                    return;
                }

                var embeddings = await this.ResolveEmbeddingsAsync(symbols, cancellationToken);
                bool hasSymbolsVec = await this.TableExistsAsync("symbols_vec", cancellationToken);
                var sqlBuilder = new StringBuilder("BEGIN IMMEDIATE;").AppendLine();
                var parameters = new Dictionary<string, object?>();

                for (int index = 0; index < symbols.Count; index++)
                {
                    AppendSymbolUpsert(sqlBuilder, parameters, symbols[index], embeddings[index], hasSymbolsVec, index);
                }

                _ = sqlBuilder.AppendLine("COMMIT;");
                await this._sqliteRunner.ExecuteAsync(sqlBuilder.ToString(), parameters, cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);

        return this.RunOperationAsync(
            "upsert-edge-batch",
            async activity =>
            {
                activity?.SetTag("graphrag.edge.count", edges.Count);

                if (edges.Count == 0)
                {
                    return;
                }

                var sqlBuilder = new StringBuilder("BEGIN IMMEDIATE;").AppendLine();
                var parameters = new Dictionary<string, object?>(edges.Count * 9);

                for (int index = 0; index < edges.Count; index++)
                {
                    AppendEdgeUpsert(sqlBuilder, parameters, edges[index], index);
                }

                _ = sqlBuilder.AppendLine("COMMIT;");
                await this._sqliteRunner.ExecuteAsync(sqlBuilder.ToString(), parameters, cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "delete-symbols-by-file",
            async activity =>
            {
                activity?.SetTag("graphrag.file.id", fileId);

                bool hasSymbolsVec = await this.TableExistsAsync("symbols_vec", cancellationToken);
                string sql = hasSymbolsVec
                    ? """
                      BEGIN IMMEDIATE;

                      DELETE FROM edges
                      WHERE source_id IN (SELECT id FROM symbols WHERE file_id = @fileId)
                         OR target_id IN (SELECT id FROM symbols WHERE file_id = @fileId);

                      DELETE FROM unresolved_call_sites
                      WHERE source_file_id = @fileId;

                      DELETE FROM symbols_vec
                      WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @fileId);

                      DELETE FROM symbols
                      WHERE file_id = @fileId;

                      COMMIT;
                      """
                    : """
                      BEGIN IMMEDIATE;

                      DELETE FROM edges
                      WHERE source_id IN (SELECT id FROM symbols WHERE file_id = @fileId)
                         OR target_id IN (SELECT id FROM symbols WHERE file_id = @fileId);

                      DELETE FROM unresolved_call_sites
                      WHERE source_file_id = @fileId;

                      DELETE FROM symbols
                      WHERE file_id = @fileId;

                      COMMIT;
                      """;

                await this._sqliteRunner.ExecuteAsync(
                    sql,
                    new Dictionary<string, object?> { ["fileId"] = ToDbGuid(fileId) },
                    cancellationToken);
            });

    /// <inheritdoc />
    public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "delete-file",
            async activity =>
            {
                activity?.SetTag("graphrag.file.id", fileId);

                bool hasSymbolsVec = await this.TableExistsAsync("symbols_vec", cancellationToken);
                string sql = hasSymbolsVec
                    ? """
                      BEGIN IMMEDIATE;

                      DELETE FROM edges
                      WHERE source_id = @fileId
                         OR target_id = @fileId
                         OR source_id IN (SELECT id FROM modules WHERE file_id = @fileId)
                         OR target_id IN (SELECT id FROM modules WHERE file_id = @fileId)
                         OR source_id IN (SELECT id FROM symbols WHERE file_id = @fileId)
                         OR target_id IN (SELECT id FROM symbols WHERE file_id = @fileId);

                      DELETE FROM unresolved_call_sites
                      WHERE source_file_id = @fileId;

                      DELETE FROM symbols_vec
                      WHERE symbol_id IN (SELECT id FROM symbols WHERE file_id = @fileId);

                      DELETE FROM symbols
                      WHERE file_id = @fileId;

                      DELETE FROM modules
                      WHERE file_id = @fileId;

                      DELETE FROM files
                      WHERE id = @fileId;

                      COMMIT;
                      """
                    : """
                      BEGIN IMMEDIATE;

                      DELETE FROM edges
                      WHERE source_id = @fileId
                         OR target_id = @fileId
                         OR source_id IN (SELECT id FROM modules WHERE file_id = @fileId)
                         OR target_id IN (SELECT id FROM modules WHERE file_id = @fileId)
                         OR source_id IN (SELECT id FROM symbols WHERE file_id = @fileId)
                         OR target_id IN (SELECT id FROM symbols WHERE file_id = @fileId);

                      DELETE FROM unresolved_call_sites
                      WHERE source_file_id = @fileId;

                      DELETE FROM symbols
                      WHERE file_id = @fileId;

                      DELETE FROM modules
                      WHERE file_id = @fileId;

                      DELETE FROM files
                      WHERE id = @fileId;

                      COMMIT;
                      """;

                await this._sqliteRunner.ExecuteAsync(
                    sql,
                    new Dictionary<string, object?> { ["fileId"] = ToDbGuid(fileId) },
                    cancellationToken);
            });

    /// <inheritdoc />
    public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPath))
        {
            throw new ArgumentException("New path cannot be null or whitespace.", nameof(newPath));
        }

        return this.RunOperationAsync(
            "rename-file",
            activity =>
            {
                activity?.SetTag("graphrag.file.id", fileId);
                activity?.SetTag("graphrag.file.path", newPath);

                return this._sqliteRunner.ExecuteAsync(
                    """
                    UPDATE files
                    SET path = @path
                    WHERE id = @fileId;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["fileId"] = ToDbGuid(fileId),
                        ["path"] = newPath,
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "load-indexed-commit",
            async activity =>
            {
                activity?.SetTag("graphrag.repo.id", repoId);

                List<string?> results = await this._sqliteRunner.QueryAsync(
                    """
                    SELECT indexed_commit
                    FROM repos
                    WHERE id = @repoId
                    LIMIT 1;
                    """,
                    reader => Task.FromResult(reader.IsDBNull(0) ? null : reader.GetString(0)),
                    new Dictionary<string, object?> { ["repoId"] = ToDbGuid(repoId) },
                    cancellationToken);

                return results.Count == 0 ? null : results[0];
            });

    /// <inheritdoc />
    public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(indexedCommit))
        {
            throw new ArgumentException("Indexed commit cannot be null or whitespace.", nameof(indexedCommit));
        }

        return this.RunOperationAsync(
            "set-indexed-commit",
            activity =>
            {
                activity?.SetTag("graphrag.repo.id", repoId);

                return this._sqliteRunner.ExecuteAsync(
                    """
                    UPDATE repos
                    SET indexed_commit = @indexedCommit,
                        indexed_at = @indexedAt
                    WHERE id = @repoId;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["repoId"] = ToDbGuid(repoId),
                        ["indexedCommit"] = indexedCommit,
                        ["indexedAt"] = ToDbDateTime(DateTimeOffset.UtcNow),
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default)
    {
        ValidateVectorSearchArguments(queryText, topK);

        return this.RunOperationAsync(
            "vector-search-symbols",
            async activity =>
            {
                activity?.SetTag("graphrag.search.top_k", topK);
                activity?.SetTag("graphrag.search.type", "symbols");

                float[] queryEmbedding = (await this._embeddingGenerator.GenerateEmbeddingAsync(queryText, cancellationToken)).ToArray();
                IReadOnlyList<VectorSearchResult> results = await this.TryVectorTableSearchAsync("symbols_vec", "symbol_id", queryEmbedding, topK, cancellationToken);
                if (results.Count > 0)
                {
                    activity?.SetTag("graphrag.search.backend", "sqlite-vec");
                    activity?.SetTag("graphrag.search.result_count", results.Count);
                    return results;
                }

                activity?.SetTag("graphrag.search.backend", "fallback");
                results = await this.FallbackVectorSearchAsync("symbols", "id", queryEmbedding, topK, cancellationToken);
                activity?.SetTag("graphrag.search.result_count", results.Count);
                return results;
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default)
    {
        ValidateVectorSearchArguments(queryText, topK);

        return this.RunOperationAsync(
            "vector-search-clusters",
            async activity =>
            {
                activity?.SetTag("graphrag.search.top_k", topK);
                activity?.SetTag("graphrag.search.type", "clusters");

                float[] queryEmbedding = (await this._embeddingGenerator.GenerateEmbeddingAsync(queryText, cancellationToken)).ToArray();
                IReadOnlyList<VectorSearchResult> results = await this.TryVectorTableSearchAsync("clusters_vec", "cluster_id", queryEmbedding, topK, cancellationToken);
                if (results.Count > 0)
                {
                    activity?.SetTag("graphrag.search.backend", "sqlite-vec");
                    activity?.SetTag("graphrag.search.result_count", results.Count);
                    return results;
                }

                activity?.SetTag("graphrag.search.backend", "fallback");
                results = await this.FallbackVectorSearchAsync("clusters", "id", queryEmbedding, topK, cancellationToken);
                activity?.SetTag("graphrag.search.result_count", results.Count);
                return results;
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SeedSymbolIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<TraversalHop>>([]);
        }

        if (request.MaxHops is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MaxHops must be between 1 and 6.");
        }

        return this.RunOperationAsync(
            "traverse-from",
            async activity =>
            {
                activity?.SetTag("graphrag.traversal.seed_count", request.SeedSymbolIds.Count);
                activity?.SetTag("graphrag.traversal.max_hops", request.MaxHops);
                activity?.SetTag("graphrag.traversal.direction", request.Direction.ToString());

                string sql = BuildTraversalSql(request, out Dictionary<string, object?> parameters);
                List<TraversalHop> hops = await this._sqliteRunner.QueryAsync(
                    sql,
                    reader => Task.FromResult(HydrateTraversalHop(reader)),
                    parameters,
                    cancellationToken);

                activity?.SetTag("graphrag.traversal.result_count", hops.Count);
                return (IReadOnlyList<TraversalHop>)hops;
            });
    }

    /// <inheritdoc />
    public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "get-symbol-by-id",
            async activity =>
            {
                activity?.SetTag("graphrag.symbol.id", symbolId);

                List<Symbol> results = await this._sqliteRunner.QueryAsync(
                    """
                    SELECT id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                           embedding, content_hash, is_utility, source_range_start, source_range_end
                    FROM symbols
                    WHERE id = @symbolId
                    LIMIT 1;
                    """,
                    reader => Task.FromResult(HydrateSymbol(reader)),
                    new Dictionary<string, object?> { ["symbolId"] = ToDbGuid(symbolId) },
                    cancellationToken);

                return results.Count == 0 ? null : results[0];
            });

    /// <inheritdoc />
    public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Symbol name cannot be null or whitespace.", nameof(name));
        }

        return this.RunOperationAsync(
            "find-symbols-by-name",
            async activity =>
            {
                activity?.SetTag("graphrag.symbol.name", name);

                bool hasFts = await this.TableExistsAsync("symbols_fts", cancellationToken);
                string ftsQuery = BuildFtsMatchQuery(name);
                string sql = hasFts && !string.IsNullOrWhiteSpace(ftsQuery)
                    ? """
                      WITH ranked_matches AS (
                          SELECT rowid AS symbol_rowid, 0 AS match_rank
                          FROM symbols
                          WHERE name = @name

                          UNION ALL

                          SELECT rowid AS symbol_rowid, 1 AS match_rank
                          FROM symbols_fts
                          WHERE symbols_fts MATCH @ftsQuery
                      ),
                      deduped_matches AS (
                          SELECT symbol_rowid, MIN(match_rank) AS match_rank
                          FROM ranked_matches
                          GROUP BY symbol_rowid
                      )
                      SELECT s.id, s.file_id, s.module_id, s.name, s.fully_qualified_name, s.kind, s.signature, s.summary, s.one_line_summary,
                             s.embedding, s.content_hash, s.is_utility, s.source_range_start, s.source_range_end
                      FROM deduped_matches AS m
                      JOIN symbols AS s
                        ON s.rowid = m.symbol_rowid
                      ORDER BY m.match_rank ASC, s.name ASC, s.fully_qualified_name ASC;
                      """
                    : """
                      SELECT id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                             embedding, content_hash, is_utility, source_range_start, source_range_end
                      FROM symbols
                      WHERE name = @name
                      ORDER BY name ASC, fully_qualified_name ASC;
                      """;

                List<Symbol> matches = await this._sqliteRunner.QueryAsync(
                    sql,
                    reader => Task.FromResult(HydrateSymbol(reader)),
                    new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["ftsQuery"] = ftsQuery,
                    },
                    cancellationToken);

                activity?.SetTag("graphrag.symbol.result_count", matches.Count);
                return (IReadOnlyList<Symbol>)matches;
            });
    }

    /// <inheritdoc />
    public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callSites);

        return this.RunOperationAsync(
            "stage-unresolved-call-sites",
            async activity =>
            {
                activity?.SetTag("graphrag.call_site.count", callSites.Count);

                if (callSites.Count == 0)
                {
                    return;
                }

                var sqlBuilder = new StringBuilder("BEGIN IMMEDIATE;").AppendLine();
                var parameters = new Dictionary<string, object?>(callSites.Count * 6);

                for (int index = 0; index < callSites.Count; index++)
                {
                    AppendUnresolvedCallSiteUpsert(sqlBuilder, parameters, callSites[index], index);
                }

                _ = sqlBuilder.AppendLine("COMMIT;");
                await this._sqliteRunner.ExecuteAsync(sqlBuilder.ToString(), parameters, cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "drain-unresolved-call-sites",
            async activity =>
            {
                activity?.SetTag("graphrag.file.id", sourceFileId?.ToString("D", CultureInfo.InvariantCulture));

                await using var connection = await this.OpenConnectionAsync(cancellationToken);
                await using var beginCommand = connection.CreateCommand();
                beginCommand.CommandText = "BEGIN IMMEDIATE;";
                await beginCommand.ExecuteNonQueryAsync(cancellationToken);

                try
                {
                    List<UnresolvedCallSite> callSites = [];

                    await using (var selectCommand = connection.CreateCommand())
                    {
                        selectCommand.CommandText =
                            """
                            SELECT id, source_symbol_id, source_file_id, identifier, scope, llm_extracted_target
                            FROM unresolved_call_sites
                            WHERE (@hasSourceFileId = 0 OR source_file_id = @sourceFileId)
                            ORDER BY rowid ASC;
                            """;
                        _ = selectCommand.Parameters.AddWithValue("@hasSourceFileId", sourceFileId.HasValue ? 1 : 0);
                        _ = selectCommand.Parameters.AddWithValue("@sourceFileId", sourceFileId.HasValue ? ToDbGuid(sourceFileId.Value) : DBNull.Value);

                        await using DbDataReader reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            callSites.Add(HydrateUnresolvedCallSite(reader));
                        }
                    }

                    await using (var deleteCommand = connection.CreateCommand())
                    {
                        deleteCommand.CommandText =
                            """
                            DELETE FROM unresolved_call_sites
                            WHERE (@hasSourceFileId = 0 OR source_file_id = @sourceFileId);
                            """;
                        _ = deleteCommand.Parameters.AddWithValue("@hasSourceFileId", sourceFileId.HasValue ? 1 : 0);
                        _ = deleteCommand.Parameters.AddWithValue("@sourceFileId", sourceFileId.HasValue ? ToDbGuid(sourceFileId.Value) : DBNull.Value);
                        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using var commitCommand = connection.CreateCommand();
                    commitCommand.CommandText = "COMMIT;";
                    await commitCommand.ExecuteNonQueryAsync(cancellationToken);

                    activity?.SetTag("graphrag.call_site.result_count", callSites.Count);
                    return (IReadOnlyList<UnresolvedCallSite>)callSites;
                }
                catch
                {
                    await using var rollbackCommand = connection.CreateCommand();
                    rollbackCommand.CommandText = "ROLLBACK;";
                    await rollbackCommand.ExecuteNonQueryAsync(CancellationToken.None);
                    throw;
                }
            });

    /// <inheritdoc />
    public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);

        return this.RunOperationAsync(
            "apply-cluster-assignments",
            async activity =>
            {
                activity?.SetTag("graphrag.assignment.count", assignments.Count);

                if (assignments.Count == 0)
                {
                    return;
                }

                List<Edge> edges =
                [
                    .. assignments.Select(kvp => new Edge
                    {
                        Id = CreateDeterministicGuid(kvp.Key.ToString("D", CultureInfo.InvariantCulture), kvp.Value.Item1.ToString("D", CultureInfo.InvariantCulture), kvp.Value.Item2),
                        SourceId = kvp.Key,
                        SourceKind = "symbol",
                        TargetId = kvp.Value.Item1,
                        TargetKind = "cluster",
                        EdgeKind = EdgeKind.MemberOf,
                        Confidence = 1.0,
                        Signals = [],
                        Properties = new Dictionary<string, object?> { ["kind"] = kvp.Value.Item2 },
                    }),
                ];

                await this.UpsertEdgeBatchAsync(edges, cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        return this.RunOperationAsync(
            "replace-cluster-summaries",
            async activity =>
            {
                activity?.SetTag("graphrag.cluster.count", clusters.Count);

                IReadOnlyList<float[]?> embeddings = await this.ResolveClusterEmbeddingsAsync(clusters, cancellationToken);
                bool hasClustersVec = await this.TableExistsAsync("clusters_vec", cancellationToken);
                var sqlBuilder = new StringBuilder("BEGIN IMMEDIATE;").AppendLine();
                var parameters = new Dictionary<string, object?>();

                _ = sqlBuilder.AppendLine("DELETE FROM edges WHERE edge_kind = 'MemberOf';");
                if (hasClustersVec)
                {
                    _ = sqlBuilder.AppendLine("DELETE FROM clusters_vec;");
                }

                _ = sqlBuilder.AppendLine("DELETE FROM clusters;");

                for (int index = 0; index < clusters.Count; index++)
                {
                    AppendClusterInsert(sqlBuilder, parameters, clusters[index], embeddings[index], hasClustersVec, index);
                }

                _ = sqlBuilder.AppendLine("COMMIT;");
                await this._sqliteRunner.ExecuteAsync(sqlBuilder.ToString(), parameters, cancellationToken);
            });
    }

    private async Task UpsertSymbolCoreAsync(Symbol symbol, CancellationToken cancellationToken)
    {
        float[]? embedding = await this.ResolveEmbeddingAsync(symbol, cancellationToken);
        bool hasSymbolsVec = await this.TableExistsAsync("symbols_vec", cancellationToken);

        var sqlBuilder = new StringBuilder("BEGIN IMMEDIATE;").AppendLine();
        var parameters = new Dictionary<string, object?>();
        AppendSymbolUpsert(sqlBuilder, parameters, symbol, embedding, hasSymbolsVec, null);
        _ = sqlBuilder.AppendLine("COMMIT;");

        await this._sqliteRunner.ExecuteAsync(sqlBuilder.ToString(), parameters, cancellationToken);
    }

    private async Task<IReadOnlyList<float[]?>> ResolveEmbeddingsAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken)
    {
        List<string> pendingTexts = [];
        List<int> pendingIndexes = [];
        float[]?[] embeddings = new float[]?[symbols.Count];

        for (int index = 0; index < symbols.Count; index++)
        {
            Symbol symbol = symbols[index];
            if (symbol.Embedding is { Length: > 0 })
            {
                embeddings[index] = symbol.Embedding;
                continue;
            }

            string? text = GetEmbeddingSourceText(symbol);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            pendingTexts.Add(text);
            pendingIndexes.Add(index);
        }

        if (pendingTexts.Count > 0)
        {
            IReadOnlyList<ReadOnlyMemory<float>> generated = await this._embeddingGenerator.GenerateEmbeddingsAsync(pendingTexts, cancellationToken);
            for (int index = 0; index < generated.Count; index++)
            {
                embeddings[pendingIndexes[index]] = generated[index].ToArray();
            }
        }

        return embeddings;
    }

    private async Task<IReadOnlyList<float[]?>> ResolveClusterEmbeddingsAsync(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters, CancellationToken cancellationToken)
    {
        List<string> pendingTexts = [];
        List<int> pendingIndexes = [];
        float[]?[] embeddings = new float[]?[clusters.Count];

        for (int index = 0; index < clusters.Count; index++)
        {
            Agency.GraphRAG.Code.Domain.Cluster cluster = clusters[index];
            if (cluster.Embedding is { Length: > 0 })
            {
                embeddings[index] = cluster.Embedding;
                continue;
            }

            string? text = string.IsNullOrWhiteSpace(cluster.Summary) ? cluster.Label : cluster.Summary;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            pendingTexts.Add(text);
            pendingIndexes.Add(index);
        }

        if (pendingTexts.Count > 0)
        {
            IReadOnlyList<ReadOnlyMemory<float>> generated = await this._embeddingGenerator.GenerateEmbeddingsAsync(pendingTexts, cancellationToken);
            for (int index = 0; index < generated.Count; index++)
            {
                embeddings[pendingIndexes[index]] = generated[index].ToArray();
            }
        }

        return embeddings;
    }

    private async Task<float[]?> ResolveEmbeddingAsync(Symbol symbol, CancellationToken cancellationToken)
    {
        if (symbol.Embedding is { Length: > 0 })
        {
            return symbol.Embedding;
        }

        string? text = GetEmbeddingSourceText(symbol);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ReadOnlyMemory<float> embedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken);
        return embedding.ToArray();
    }

    private static void AppendSymbolUpsert(
        StringBuilder sqlBuilder,
        Dictionary<string, object?> parameters,
        Symbol symbol,
        float[]? embedding,
        bool hasSymbolsVec,
        int? index)
    {
        string suffix = index?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        string sql =
            """
            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES (
                @id, @fileId, @moduleId, @name, @fullyQualifiedName, @kind, @signature, @summary, @oneLineSummary,
                @embedding, @contentHash, @isUtility, @sourceRangeStart, @sourceRangeEnd
            )
            ON CONFLICT (id) DO UPDATE
            SET file_id = excluded.file_id,
                module_id = excluded.module_id,
                name = excluded.name,
                fully_qualified_name = excluded.fully_qualified_name,
                kind = excluded.kind,
                signature = excluded.signature,
                summary = excluded.summary,
                one_line_summary = excluded.one_line_summary,
                embedding = excluded.embedding,
                content_hash = excluded.content_hash,
                is_utility = excluded.is_utility,
                source_range_start = excluded.source_range_start,
                source_range_end = excluded.source_range_end;
            """;

        _ = sqlBuilder.AppendLine(sql
            .Replace("@id", "@id" + suffix, StringComparison.Ordinal)
            .Replace("@fileId", "@fileId" + suffix, StringComparison.Ordinal)
            .Replace("@moduleId", "@moduleId" + suffix, StringComparison.Ordinal)
            .Replace("@name", "@name" + suffix, StringComparison.Ordinal)
            .Replace("@fullyQualifiedName", "@fullyQualifiedName" + suffix, StringComparison.Ordinal)
            .Replace("@kind", "@kind" + suffix, StringComparison.Ordinal)
            .Replace("@signature", "@signature" + suffix, StringComparison.Ordinal)
            .Replace("@summary", "@summary" + suffix, StringComparison.Ordinal)
            .Replace("@oneLineSummary", "@oneLineSummary" + suffix, StringComparison.Ordinal)
            .Replace("@embedding", "@embedding" + suffix, StringComparison.Ordinal)
            .Replace("@contentHash", "@contentHash" + suffix, StringComparison.Ordinal)
            .Replace("@isUtility", "@isUtility" + suffix, StringComparison.Ordinal)
            .Replace("@sourceRangeStart", "@sourceRangeStart" + suffix, StringComparison.Ordinal)
            .Replace("@sourceRangeEnd", "@sourceRangeEnd" + suffix, StringComparison.Ordinal));

        parameters["id" + suffix] = ToDbGuid(symbol.Id);
        parameters["fileId" + suffix] = ToDbGuid(symbol.FileId);
        parameters["moduleId" + suffix] = symbol.ModuleId is null ? null : ToDbGuid(symbol.ModuleId.Value);
        parameters["name" + suffix] = symbol.Name;
        parameters["fullyQualifiedName" + suffix] = symbol.FullyQualifiedName;
        parameters["kind" + suffix] = symbol.Kind.ToString();
        parameters["signature" + suffix] = symbol.Signature;
        parameters["summary" + suffix] = symbol.Summary;
        parameters["oneLineSummary" + suffix] = symbol.OneLineSummary;
        parameters["embedding" + suffix] = embedding is null ? null : SerializeEmbedding(embedding);
        parameters["contentHash" + suffix] = symbol.ContentHash;
        parameters["isUtility" + suffix] = symbol.IsUtility ? 1 : 0;
        parameters["sourceRangeStart" + suffix] = symbol.SourceRangeStart;
        parameters["sourceRangeEnd" + suffix] = symbol.SourceRangeEnd;

        if (hasSymbolsVec)
        {
            parameters["symbolVector" + suffix] = embedding is null ? null : FormatVector(embedding);
            AppendVectorTableUpsert(sqlBuilder, "symbols_vec", "symbol_id", "id" + suffix, "symbolVector" + suffix, embedding is not null);
        }
    }

    private static void AppendEdgeUpsert(StringBuilder sqlBuilder, Dictionary<string, object?> parameters, Edge edge, int index)
    {
        string suffix = index.ToString(CultureInfo.InvariantCulture);
        string sql =
            """
            DELETE FROM edges
            WHERE source_id = @sourceId
              AND source_kind = @sourceKind
              AND target_id = @targetId
              AND target_kind = @targetKind
              AND edge_kind = @edgeKind;

            INSERT INTO edges (id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties)
            VALUES (@id, @sourceId, @sourceKind, @targetId, @targetKind, @edgeKind, @confidence, @signals, @properties);
            """;

        _ = sqlBuilder.AppendLine(sql
            .Replace("@id", "@id" + suffix, StringComparison.Ordinal)
            .Replace("@sourceId", "@sourceId" + suffix, StringComparison.Ordinal)
            .Replace("@sourceKind", "@sourceKind" + suffix, StringComparison.Ordinal)
            .Replace("@targetId", "@targetId" + suffix, StringComparison.Ordinal)
            .Replace("@targetKind", "@targetKind" + suffix, StringComparison.Ordinal)
            .Replace("@edgeKind", "@edgeKind" + suffix, StringComparison.Ordinal)
            .Replace("@confidence", "@confidence" + suffix, StringComparison.Ordinal)
            .Replace("@signals", "@signals" + suffix, StringComparison.Ordinal)
            .Replace("@properties", "@properties" + suffix, StringComparison.Ordinal));

        parameters["id" + suffix] = ToDbGuid(edge.Id);
        parameters["sourceId" + suffix] = ToDbGuid(edge.SourceId);
        parameters["sourceKind" + suffix] = edge.SourceKind;
        parameters["targetId" + suffix] = ToDbGuid(edge.TargetId);
        parameters["targetKind" + suffix] = edge.TargetKind;
        parameters["edgeKind" + suffix] = edge.EdgeKind.ToString();
        parameters["confidence" + suffix] = edge.Confidence;
        parameters["signals" + suffix] = SerializeSignals(edge.Signals);
        parameters["properties" + suffix] = SerializeProperties(edge.Properties);
    }

    private static void AppendUnresolvedCallSiteUpsert(StringBuilder sqlBuilder, Dictionary<string, object?> parameters, UnresolvedCallSite callSite, int index)
    {
        string suffix = index.ToString(CultureInfo.InvariantCulture);
        string sql =
            """
            INSERT INTO unresolved_call_sites (id, source_symbol_id, source_file_id, identifier, scope, llm_extracted_target)
            VALUES (__ID__, __SOURCE_SYMBOL_ID__, __SOURCE_FILE_ID__, __IDENTIFIER__, __SCOPE__, __LLM_EXTRACTED_TARGET__)
            ON CONFLICT (id) DO UPDATE
            SET source_symbol_id = excluded.source_symbol_id,
                source_file_id = excluded.source_file_id,
                identifier = excluded.identifier,
                scope = excluded.scope,
                llm_extracted_target = excluded.llm_extracted_target;
            """;

        _ = sqlBuilder.AppendLine(sql
            .Replace("__ID__", "@id" + suffix, StringComparison.Ordinal)
            .Replace("__SOURCE_SYMBOL_ID__", "@sourceSymbolId" + suffix, StringComparison.Ordinal)
            .Replace("__SOURCE_FILE_ID__", "@sourceFileId" + suffix, StringComparison.Ordinal)
            .Replace("__IDENTIFIER__", "@identifier" + suffix, StringComparison.Ordinal)
            .Replace("__SCOPE__", "@scope" + suffix, StringComparison.Ordinal)
            .Replace("__LLM_EXTRACTED_TARGET__", "@llmExtractedTarget" + suffix, StringComparison.Ordinal));

        parameters["id" + suffix] = ToDbGuid(callSite.Id);
        parameters["sourceSymbolId" + suffix] = ToDbGuid(callSite.SourceSymbolId);
        parameters["sourceFileId" + suffix] = ToDbGuid(callSite.SourceFileId);
        parameters["identifier" + suffix] = callSite.Identifier;
        parameters["scope" + suffix] = callSite.Scope;
        parameters["llmExtractedTarget" + suffix] = callSite.LlmExtractedTarget;
    }

    private static void AppendClusterInsert(
        StringBuilder sqlBuilder,
        Dictionary<string, object?> parameters,
        Agency.GraphRAG.Code.Domain.Cluster cluster,
        float[]? embedding,
        bool hasClustersVec,
        int index)
    {
        string suffix = index.ToString(CultureInfo.InvariantCulture);
        string sql =
            """
            INSERT INTO clusters (id, label, summary, embedding, coherence, type, level)
            VALUES (@id, @label, @summary, @embedding, @coherence, @type, @level);
            """;

        _ = sqlBuilder.AppendLine(sql
            .Replace("@id", "@id" + suffix, StringComparison.Ordinal)
            .Replace("@label", "@label" + suffix, StringComparison.Ordinal)
            .Replace("@summary", "@summary" + suffix, StringComparison.Ordinal)
            .Replace("@embedding", "@embedding" + suffix, StringComparison.Ordinal)
            .Replace("@coherence", "@coherence" + suffix, StringComparison.Ordinal)
            .Replace("@type", "@type" + suffix, StringComparison.Ordinal)
            .Replace("@level", "@level" + suffix, StringComparison.Ordinal));

        parameters["id" + suffix] = ToDbGuid(cluster.Id);
        parameters["label" + suffix] = cluster.Label;
        parameters["summary" + suffix] = cluster.Summary;
        parameters["embedding" + suffix] = embedding is null ? null : SerializeEmbedding(embedding);
        parameters["coherence" + suffix] = cluster.CoherenceScore;
        parameters["type" + suffix] = cluster.Type.ToString();
        parameters["level" + suffix] = 0;

        if (hasClustersVec)
        {
            parameters["clusterVector" + suffix] = embedding is null ? null : FormatVector(embedding);
            AppendVectorTableUpsert(sqlBuilder, "clusters_vec", "cluster_id", "id" + suffix, "clusterVector" + suffix, embedding is not null);
        }
    }

    private static void AppendVectorTableUpsert(
        StringBuilder sqlBuilder,
        string tableName,
        string idColumnName,
        string idParameterName,
        string embeddingParameterName,
        bool includeInsert)
    {
        _ = sqlBuilder.AppendLine($"DELETE FROM {tableName} WHERE {idColumnName} = @{idParameterName};");
        if (includeInsert)
        {
            _ = sqlBuilder.AppendLine($"INSERT INTO {tableName} ({idColumnName}, embedding) VALUES (@{idParameterName}, @{embeddingParameterName});");
        }
    }

    private async Task RunOperationAsync(string operationName, Func<Activity?, Task> action)
    {
        using var activity = _activitySource.StartActivity($"graphrag.sqlite.{operationName}", ActivityKind.Client);
        activity?.SetTag("graphrag.operation", operationName);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Starting SQLite graph store operation {Operation}", operationName);

        try
        {
            await action(activity);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", operationName }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", operationName } });
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Completed SQLite graph store operation {Operation} in {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", operationName }, { "status", "error" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", operationName } });
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));
            this._logger.LogError(ex, "SQLite graph store operation {Operation} failed after {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private async Task<T> RunOperationAsync<T>(string operationName, Func<Activity?, Task<T>> action)
    {
        using var activity = _activitySource.StartActivity($"graphrag.sqlite.{operationName}", ActivityKind.Client);
        activity?.SetTag("graphrag.operation", operationName);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Starting SQLite graph store operation {Operation}", operationName);

        try
        {
            T result = await action(activity);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", operationName }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", operationName } });
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Completed SQLite graph store operation {Operation} in {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", operationName }, { "status", "error" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", operationName } });
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));
            this._logger.LogError(ex, "SQLite graph store operation {Operation} failed after {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private async Task<IReadOnlyList<VectorSearchResult>> TryVectorTableSearchAsync(
        string tableName,
        string idColumnName,
        float[] queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        if (!await this.TableExistsAsync(tableName, cancellationToken))
        {
            return [];
        }

        try
        {
            List<VectorSearchResult> results = await this._sqliteRunner.QueryAsync(
                $"""
                SELECT {idColumnName}, vec_distance_cosine(embedding, @queryEmbedding) AS distance
                FROM {tableName}
                ORDER BY distance ASC
                LIMIT @topK;
                """,
                reader => Task.FromResult(
                    new VectorSearchResult
                    {
                        Id = ParseGuid(reader.GetString(0)),
                        Score = DistanceToSimilarity(reader.GetDouble(1)),
                    }),
                new Dictionary<string, object?>
                {
                    ["queryEmbedding"] = FormatVector(queryEmbedding),
                    ["topK"] = topK,
                },
                cancellationToken);

            return results;
        }
        catch (SqliteException ex)
        {
            this._logger.LogWarning(ex, "sqlite-vec search against {TableName} failed. Falling back to managed cosine search.", tableName);
            return [];
        }
    }

    private async Task<IReadOnlyList<VectorSearchResult>> FallbackVectorSearchAsync(
        string tableName,
        string idColumnName,
        float[] queryEmbedding,
        int topK,
        CancellationToken cancellationToken)
    {
        List<(Guid Id, float[] Embedding)> rows = await this._sqliteRunner.QueryAsync(
            $"""
            SELECT {idColumnName}, embedding
            FROM {tableName}
            WHERE embedding IS NOT NULL;
            """,
            reader => Task.FromResult((ParseGuid(reader.GetString(0)), DeserializeEmbedding(reader.GetValue(1)) ?? [])),
            null,
            cancellationToken);

        return rows
            .Where(row => row.Embedding.Length > 0)
            .Select(row => new VectorSearchResult
            {
                Id = row.Id,
                Score = CosineSimilarity(queryEmbedding, row.Embedding),
            })
            .OrderByDescending(result => result.Score)
            .Take(topK)
            .ToList();
    }

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        List<long> counts = await this._sqliteRunner.QueryAsync(
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type IN ('table', 'view')
              AND name = @tableName;
            """,
            reader => Task.FromResult(reader.GetInt64(0)),
            new Dictionary<string, object?> { ["tableName"] = tableName },
            cancellationToken);

        return counts.Count > 0 && counts[0] > 0;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(this._connectionString);
        await connection.OpenAsync(cancellationToken);
        SqliteMigrationRunner.ConfigureConnection(connection);
        return connection;
    }

    private static string BuildTraversalSql(TraversalRequest request, out Dictionary<string, object?> parameters)
    {
        parameters = new Dictionary<string, object?>
        {
            ["maxHops"] = request.MaxHops,
            ["minConfidence"] = request.MinConfidence,
        };

        var seedsBuilder = new StringBuilder();
        for (int index = 0; index < request.SeedSymbolIds.Count; index++)
        {
            if (index > 0)
            {
                _ = seedsBuilder.Append(", ");
            }

            string parameterName = "seed" + index.ToString(CultureInfo.InvariantCulture);
            _ = seedsBuilder.Append('(').Append('@').Append(parameterName).Append(')');
            parameters[parameterName] = ToDbGuid(request.SeedSymbolIds[index]);
        }

        string edgeFilter = string.Empty;
        if (request.EdgeKinds.Count > 0)
        {
            var edgeKindParameters = new List<string>(request.EdgeKinds.Count);
            for (int index = 0; index < request.EdgeKinds.Count; index++)
            {
                string parameterName = "edgeKind" + index.ToString(CultureInfo.InvariantCulture);
                edgeKindParameters.Add("@" + parameterName);
                parameters[parameterName] = request.EdgeKinds[index].ToString();
            }

            edgeFilter = $" AND e.edge_kind IN ({string.Join(", ", edgeKindParameters)})";
        }

        List<string> branches = [];
        if (request.Direction is TraversalDirection.Outgoing or TraversalDirection.Both)
        {
            branches.Add(
                $"""
                SELECT e.target_id AS symbol_id,
                       t.depth + 1 AS depth,
                       e.id AS via_edge_id,
                       t.path || e.target_id || ',' AS path
                FROM traversal AS t
                JOIN edges AS e
                  ON e.source_id = t.symbol_id
                 AND e.source_kind = 'symbol'
                 AND e.target_kind = 'symbol'
                WHERE t.depth < @maxHops
                  AND e.confidence >= @minConfidence{edgeFilter}
                  AND instr(t.path, ',' || e.target_id || ',') = 0
                """);
        }

        if (request.Direction is TraversalDirection.Incoming or TraversalDirection.Both)
        {
            branches.Add(
                $"""
                SELECT e.source_id AS symbol_id,
                       t.depth + 1 AS depth,
                       e.id AS via_edge_id,
                       t.path || e.source_id || ',' AS path
                FROM traversal AS t
                JOIN edges AS e
                  ON e.target_id = t.symbol_id
                 AND e.source_kind = 'symbol'
                 AND e.target_kind = 'symbol'
                WHERE t.depth < @maxHops
                  AND e.confidence >= @minConfidence{edgeFilter}
                  AND instr(t.path, ',' || e.source_id || ',') = 0
                """);
        }

        string recursiveBranchSql = string.Join(Environment.NewLine + "UNION ALL" + Environment.NewLine, branches);
        return $"""
               WITH RECURSIVE seed_symbols(symbol_id) AS (
                   VALUES {seedsBuilder}
               ),
               traversal(symbol_id, depth, via_edge_id, path) AS (
                   SELECT symbol_id,
                          0 AS depth,
                          NULL AS via_edge_id,
                          ',' || symbol_id || ',' AS path
                   FROM seed_symbols

                   UNION ALL

                   {recursiveBranchSql}
               )
               SELECT traversal.symbol_id,
                      traversal.depth,
                      e.id,
                      e.source_id,
                      e.source_kind,
                      e.target_id,
                      e.target_kind,
                      e.edge_kind,
                      e.confidence,
                      e.signals,
                      e.properties
               FROM traversal
               LEFT JOIN edges AS e
                 ON e.id = traversal.via_edge_id
               ORDER BY traversal.depth ASC, traversal.symbol_id ASC, e.id ASC;
               """;
    }

    private static TraversalHop HydrateTraversalHop(DbDataReader reader) =>
        new()
        {
            SymbolId = ParseGuid(reader.GetString(0)),
            Depth = reader.GetInt32(1),
            ViaEdge = reader.IsDBNull(2) ? null : HydrateEdge(reader, 2),
        };

    private static Symbol HydrateSymbol(DbDataReader reader) =>
        new()
        {
            Id = ParseGuid(reader.GetString(0)),
            FileId = ParseGuid(reader.GetString(1)),
            ModuleId = reader.IsDBNull(2) ? null : ParseGuid(reader.GetString(2)),
            Name = reader.GetString(3),
            FullyQualifiedName = reader.IsDBNull(4) ? null : reader.GetString(4),
            Kind = Enum.Parse<SymbolKind>(reader.GetString(5), ignoreCase: true),
            Signature = reader.IsDBNull(6) ? null : reader.GetString(6),
            Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
            OneLineSummary = reader.IsDBNull(8) ? null : reader.GetString(8),
            Embedding = reader.IsDBNull(9) ? null : DeserializeEmbedding(reader.GetValue(9)),
            ContentHash = reader.IsDBNull(10) ? null : reader.GetString(10),
            IsUtility = reader.GetInt64(11) != 0,
            SourceRangeStart = reader.GetInt32(12),
            SourceRangeEnd = reader.GetInt32(13),
        };

    private static Edge HydrateEdge(DbDataReader reader, int offset) =>
        new()
        {
            Id = ParseGuid(reader.GetString(offset)),
            SourceId = ParseGuid(reader.GetString(offset + 1)),
            SourceKind = reader.GetString(offset + 2),
            TargetId = ParseGuid(reader.GetString(offset + 3)),
            TargetKind = reader.GetString(offset + 4),
            EdgeKind = Enum.Parse<EdgeKind>(reader.GetString(offset + 5), ignoreCase: true),
            Confidence = reader.GetDouble(offset + 6),
            Signals = DeserializeSignals(reader.GetString(offset + 7)),
            Properties = DeserializeProperties(reader.GetString(offset + 8)),
        };

    private static UnresolvedCallSite HydrateUnresolvedCallSite(DbDataReader reader) =>
        new()
        {
            Id = ParseGuid(reader.GetString(0)),
            SourceSymbolId = ParseGuid(reader.GetString(1)),
            SourceFileId = ParseGuid(reader.GetString(2)),
            Identifier = reader.GetString(3),
            Scope = reader.IsDBNull(4) ? null : reader.GetString(4),
            LlmExtractedTarget = reader.IsDBNull(5) ? null : reader.GetString(5),
        };

    private static string GetConnectionString(SqliteRunner sqliteRunner)
    {
        FieldInfo? field = typeof(SqliteRunner).GetField("_connectionString", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(sqliteRunner) is string connectionString && !string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        throw new InvalidOperationException("Unable to read the SQLite connection string from the provided runner.");
    }

    private static string? InferEcosystem(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return null;
        }

        string extension = Path.GetExtension(manifestPath);
        return extension.ToLowerInvariant() switch
        {
            ".csproj" or ".fsproj" or ".vbproj" => "nuget",
            ".json" when string.Equals(Path.GetFileName(manifestPath), "package.json", StringComparison.OrdinalIgnoreCase) => "npm",
            ".toml" when string.Equals(Path.GetFileName(manifestPath), "pyproject.toml", StringComparison.OrdinalIgnoreCase) => "pypi",
            _ => null,
        };
    }

    private static string? GetEmbeddingSourceText(Symbol symbol)
    {
        if (!string.IsNullOrWhiteSpace(symbol.OneLineSummary))
        {
            return symbol.OneLineSummary;
        }

        if (!string.IsNullOrWhiteSpace(symbol.Summary))
        {
            return symbol.Summary;
        }

        if (!string.IsNullOrWhiteSpace(symbol.FullyQualifiedName))
        {
            return symbol.FullyQualifiedName;
        }

        return string.IsNullOrWhiteSpace(symbol.Name) ? null : symbol.Name;
    }

    private static void ValidateVectorSearchArguments(string queryText, int topK)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw new ArgumentException("Query text cannot be null or whitespace.", nameof(queryText));
        }

        if (topK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topK), "topK must be greater than zero.");
        }
    }

    private static object ToDbGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string? ToDbDateTime(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes<float>(embedding);
        return bytes.ToArray();
    }

    private static float[]? DeserializeEmbedding(object dbValue) =>
        dbValue switch
        {
            byte[] blob when blob.Length == 0 => [],
            byte[] blob when blob.Length % sizeof(float) == 0 => DeserializeEmbeddingBlob(blob),
            byte[] blob => JsonSerializer.Deserialize<float[]>(Encoding.UTF8.GetString(blob)),
            string text => JsonSerializer.Deserialize<float[]>(text),
            _ => throw new InvalidOperationException($"Unsupported embedding value type '{dbValue.GetType().FullName}'."),
        };

    private static float[] DeserializeEmbeddingBlob(byte[] blob)
    {
        float[] vector = new float[blob.Length / sizeof(float)];
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] = BitConverter.ToSingle(blob, index * sizeof(float));
        }

        return vector;
    }

    private static string FormatVector(float[] embedding) =>
        "[" + string.Join(",", embedding.Select(value => value.ToString("G9", CultureInfo.InvariantCulture))) + "]";

    private static string SerializeSignals(IReadOnlyList<Signal> signals) =>
        JsonSerializer.Serialize(signals.Select(signal => signal.ToString()).ToArray());

    private static IReadOnlyList<Signal> DeserializeSignals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        string[] values = JsonSerializer.Deserialize<string[]>(json) ?? [];
        List<Signal> results = [];
        foreach (string value in values)
        {
            if (Enum.TryParse(value, ignoreCase: true, out Signal signal))
            {
                results.Add(signal);
            }
        }

        return results;
    }

    private static string SerializeProperties(IReadOnlyDictionary<string, object?> properties) =>
        JsonSerializer.Serialize(properties);

    private static IReadOnlyDictionary<string, object?> DeserializeProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value))
            : new Dictionary<string, object?>();
    }

    private static object? ConvertJsonValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out long value) => value,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString(),
        };

    private static double DistanceToSimilarity(double distance) => Math.Clamp(1.0 - distance, 0.0, 1.0);

    private static double CosineSimilarity(float[] left, float[] right)
    {
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return 0.0;
        }

        double dot = 0.0;
        double leftNorm = 0.0;
        double rightNorm = 0.0;
        for (int index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm == 0.0 || rightNorm == 0.0)
        {
            return 0.0;
        }

        return Math.Clamp(dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm)), 0.0, 1.0);
    }

    private static string BuildFtsMatchQuery(string name)
    {
        string[] terms = name.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0
            ? string.Empty
            : string.Join(" AND ", terms.Select(term => $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*"));
    }

    private static Guid CreateDeterministicGuid(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }

    private static Guid ParseGuid(string value) => Guid.Parse(value);
}
