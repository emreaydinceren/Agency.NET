using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Postgres.Migrations;
using Agency.GraphRAG.Code.Storage;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Postgres;

/// <summary>
/// Persists GraphRAG code graph data in PostgreSQL.
/// </summary>
public sealed class PostgresGraphStore : IGraphStore
{
    /// <summary>
    /// The activity source name used for graph store telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.GraphRAG.Code.Postgres";

    /// <summary>
    /// The meter name used for graph store telemetry.
    /// </summary>
    public const string MeterName = "Agency.GraphRAG.Code.Postgres";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);
    private static readonly Counter<long> _operationCount =
        _meter.CreateCounter<long>("graphrag.postgres.operations", unit: "{operation}", description: "Total number of graph store operations executed.");
    private static readonly Histogram<double> _operationDuration =
        _meter.CreateHistogram<double>("graphrag.postgres.duration", unit: "ms", description: "Duration of graph store operations in milliseconds.");

    private readonly PostgreSqlRunner _postgreSqlRunner;
    private readonly Agency.Embeddings.Common.IEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger<PostgresGraphStore> _logger;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresGraphStore"/> class.
    /// </summary>
    /// <param name="postgreSqlRunner">The PostgreSQL runner used for database operations.</param>
    /// <param name="embeddingGenerator">The embedding generator used for vector search and symbol persistence.</param>
    /// <param name="connectionString">The PostgreSQL connection string used for schema migrations.</param>
    /// <param name="logger">Optional logger.</param>
    public PostgresGraphStore(
        PostgreSqlRunner postgreSqlRunner,
        Agency.Embeddings.Common.IEmbeddingGenerator embeddingGenerator,
        string connectionString,
        ILogger<PostgresGraphStore>? logger = null)
    {
        this._postgreSqlRunner = postgreSqlRunner ?? throw new ArgumentNullException(nameof(postgreSqlRunner));
        this._embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or whitespace.", nameof(connectionString));
        }

        this._connectionString = connectionString;
        this._logger = logger ?? NullLogger<PostgresGraphStore>.Instance;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresGraphStore"/> class.
    /// </summary>
    /// <param name="postgreSqlRunner">The PostgreSQL runner used for database operations.</param>
    /// <param name="embeddingGenerator">The embedding generator used for vector search and symbol persistence.</param>
    /// <param name="logger">Optional logger.</param>
    public PostgresGraphStore(
        PostgreSqlRunner postgreSqlRunner,
        Agency.Embeddings.Common.IEmbeddingGenerator embeddingGenerator,
        ILogger<PostgresGraphStore>? logger = null)
        : this(postgreSqlRunner, embeddingGenerator, GetConnectionString(postgreSqlRunner), logger)
    {
    }

    /// <inheritdoc />
    public Task InitializeSchemaAsync(CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "initialize",
            async activity =>
            {
                var migrationRunner = new PostgresMigrationRunner(this._connectionString);
                await migrationRunner.MigrateAsync(cancellationToken);
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

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
                    VALUES (@id, @remoteUrl, @rootPath, @indexedCommit::text, CASE WHEN @indexedCommit::text IS NULL THEN NULL ELSE NOW() END, @isShallow)
                    ON CONFLICT (id) DO UPDATE
                    SET remote_url = excluded.remote_url,
                        root_path = excluded.root_path,
                        indexed_commit = excluded.indexed_commit,
                        indexed_at = CASE WHEN excluded.indexed_commit IS NULL THEN repos.indexed_at ELSE NOW() END,
                        is_shallow = excluded.is_shallow;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = repo.Id,
                        ["remoteUrl"] = repo.RemoteUrl,
                        ["rootPath"] = repo.LocalPath,
                        ["indexedCommit"] = repo.IndexedCommit,
                        ["isShallow"] = repo.IsShallow,
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

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    INSERT INTO projects (id, repo_id, name, manifest_path, path, language, ecosystem)
                    VALUES (@id, @repoId, @name, @manifestPath, @path, @language, @ecosystem)
                    ON CONFLICT (id) DO UPDATE
                    SET repo_id = excluded.repo_id,
                        name = excluded.name,
                        manifest_path = excluded.manifest_path,
                        path = excluded.path,
                        language = excluded.language,
                        ecosystem = excluded.ecosystem;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = project.Id,
                        ["repoId"] = project.RepoId,
                        ["name"] = project.Name,
                        ["manifestPath"] = project.ManifestPath,
                        ["path"] = project.RelativePath,
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
            activity =>
            {
                activity?.SetTag("graphrag.package.count", packages.Count);
                if (packages.Count == 0)
                {
                    return Task.CompletedTask;
                }

                Guid[] ids = new Guid[packages.Count];
                Guid[] projectIds = new Guid[packages.Count];
                string[] names = new string[packages.Count];
                string?[] versions = new string?[packages.Count];
                string?[] versionResolved = new string?[packages.Count];
                string[] ecosystems = new string[packages.Count];
                string[] scopes = new string[packages.Count];

                for (int index = 0; index < packages.Count; index++)
                {
                    ExternalPackage package = packages[index];
                    ids[index] = package.Id;
                    projectIds[index] = package.ProjectId;
                    names[index] = package.Name;
                    versions[index] = package.Version;
                    versionResolved[index] = package.Version;
                    ecosystems[index] = package.Ecosystem;
                    scopes[index] = package.Scope;
                }

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    INSERT INTO external_packages (id, project_id, name, version, version_resolved, ecosystem, scope)
                    SELECT *
                    FROM unnest(@ids, @projectIds, @names, @versions, @versionResolved, @ecosystems, @scopes)
                        AS packages(id, project_id, name, version, version_resolved, ecosystem, scope)
                    ON CONFLICT (id) DO UPDATE
                    SET project_id = excluded.project_id,
                        name = excluded.name,
                        version = excluded.version,
                        version_resolved = excluded.version_resolved,
                        ecosystem = excluded.ecosystem,
                        scope = excluded.scope;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["ids"] = CreateArrayParameter("ids", NpgsqlDbType.Uuid, ids),
                        ["projectIds"] = CreateArrayParameter("projectIds", NpgsqlDbType.Uuid, projectIds),
                        ["names"] = CreateArrayParameter("names", NpgsqlDbType.Text, names),
                        ["versions"] = CreateArrayParameter("versions", NpgsqlDbType.Text, versions),
                        ["versionResolved"] = CreateArrayParameter("versionResolved", NpgsqlDbType.Text, versionResolved),
                        ["ecosystems"] = CreateArrayParameter("ecosystems", NpgsqlDbType.Text, ecosystems),
                        ["scopes"] = CreateArrayParameter("scopes", NpgsqlDbType.Text, scopes),
                    },
                    cancellationToken);
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

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
                    VALUES (@id, @repoId, @projectId, @path, @language, @contentHash, NOW())
                    ON CONFLICT (id) DO UPDATE
                    SET repo_id = excluded.repo_id,
                        project_id = excluded.project_id,
                        path = excluded.path,
                        language = excluded.language,
                        content_hash = excluded.content_hash,
                        last_indexed_at = NOW();
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = file.Id,
                        ["repoId"] = file.RepoId,
                        ["projectId"] = file.ProjectId,
                        ["path"] = file.Path,
                        ["language"] = file.Language,
                        ["contentHash"] = file.ContentHash,
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertModuleAsync(Domain.Module module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);

        return this.RunOperationAsync(
            "upsert-module",
            activity =>
            {
                activity?.SetTag("graphrag.module.id", module.Id);
                activity?.SetTag("graphrag.file.id", module.FileId);

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    INSERT INTO modules (id, project_id, file_id, name, path, kind)
                    VALUES (@id, (SELECT project_id FROM files WHERE id = @fileId), @fileId, @name, NULL, @kind)
                    ON CONFLICT (id) DO UPDATE
                    SET project_id = (SELECT project_id FROM files WHERE id = excluded.file_id),
                        file_id = excluded.file_id,
                        name = excluded.name,
                        path = excluded.path,
                        kind = excluded.kind;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["id"] = module.Id,
                        ["fileId"] = module.FileId,
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
            activity =>
            {
                activity?.SetTag("graphrag.symbol.id", symbol.Id);

                return this._postgreSqlRunner.ExecuteAsync(
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
                    """,
                    CreateSymbolParameters(symbol, null),
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        return this.RunOperationAsync(
            "upsert-symbol-batch",
            activity =>
            {
                activity?.SetTag("graphrag.symbol.count", symbols.Count);
                if (symbols.Count == 0)
                {
                    return Task.CompletedTask;
                }

                const string sqlPrefix =
                    """
                    INSERT INTO symbols (
                        id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                        embedding, content_hash, is_utility, source_range_start, source_range_end
                    )
                    VALUES
                    """;

                var sqlBuilder = new StringBuilder(sqlPrefix);
                var parameters = new Dictionary<string, object?>();

                for (int index = 0; index < symbols.Count; index++)
                {
                    AppendSymbolValues(sqlBuilder, parameters, symbols[index], index);
                }

                _ = sqlBuilder.AppendLine(
                    """
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
                    """);

                return this._postgreSqlRunner.ExecuteAsync(sqlBuilder.ToString(), parameters, cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);

        return this.RunOperationAsync(
            "upsert-edge-batch",
            activity =>
            {
                activity?.SetTag("graphrag.edge.count", edges.Count);
                if (edges.Count == 0)
                {
                    return Task.CompletedTask;
                }

                Guid[] ids = new Guid[edges.Count];
                Guid[] sourceIds = new Guid[edges.Count];
                string[] sourceKinds = new string[edges.Count];
                Guid[] targetIds = new Guid[edges.Count];
                string[] targetKinds = new string[edges.Count];
                string[] edgeKinds = new string[edges.Count];
                double[] confidences = new double[edges.Count];
                string[] signals = new string[edges.Count];
                string[] properties = new string[edges.Count];

                for (int index = 0; index < edges.Count; index++)
                {
                    Edge edge = edges[index];
                    ids[index] = edge.Id;
                    sourceIds[index] = edge.SourceId;
                    sourceKinds[index] = edge.SourceKind;
                    targetIds[index] = edge.TargetId;
                    targetKinds[index] = edge.TargetKind;
                    edgeKinds[index] = edge.EdgeKind.ToString();
                    confidences[index] = edge.Confidence;
                    signals[index] = SerializeSignals(edge.Signals);
                    properties[index] = SerializeProperties(edge.Properties);
                }

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    WITH edge_rows AS (
                        SELECT edge_id,
                               source_id,
                               source_kind,
                               target_id,
                               target_kind,
                               edge_kind,
                               confidence,
                               signals::jsonb AS signals,
                               properties::jsonb AS properties
                        FROM unnest(@ids, @sourceIds, @sourceKinds, @targetIds, @targetKinds, @edgeKinds, @confidences, @signals, @properties)
                            AS edge_rows(edge_id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties)
                    ),
                    deleted AS (
                        DELETE FROM edges AS existing
                        USING edge_rows AS incoming
                        WHERE existing.source_id = incoming.source_id
                          AND existing.source_kind = incoming.source_kind
                          AND existing.target_id = incoming.target_id
                          AND existing.target_kind = incoming.target_kind
                          AND existing.edge_kind = incoming.edge_kind
                    )
                    INSERT INTO edges (id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties)
                    SELECT edge_id, source_id, source_kind, target_id, target_kind, edge_kind, confidence, signals, properties
                    FROM edge_rows;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["ids"] = CreateArrayParameter("ids", NpgsqlDbType.Uuid, ids),
                        ["sourceIds"] = CreateArrayParameter("sourceIds", NpgsqlDbType.Uuid, sourceIds),
                        ["sourceKinds"] = CreateArrayParameter("sourceKinds", NpgsqlDbType.Text, sourceKinds),
                        ["targetIds"] = CreateArrayParameter("targetIds", NpgsqlDbType.Uuid, targetIds),
                        ["targetKinds"] = CreateArrayParameter("targetKinds", NpgsqlDbType.Text, targetKinds),
                        ["edgeKinds"] = CreateArrayParameter("edgeKinds", NpgsqlDbType.Text, edgeKinds),
                        ["confidences"] = CreateArrayParameter("confidences", NpgsqlDbType.Double, confidences),
                        ["signals"] = CreateArrayParameter("signals", NpgsqlDbType.Text, signals),
                        ["properties"] = CreateArrayParameter("properties", NpgsqlDbType.Text, properties),
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "delete-symbols-by-file",
            activity =>
            {
                activity?.SetTag("graphrag.file.id", fileId);

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    BEGIN;

                    DELETE FROM edges
                    WHERE source_id IN (SELECT id FROM symbols WHERE file_id = @fileId)
                       OR target_id IN (SELECT id FROM symbols WHERE file_id = @fileId);

                    DELETE FROM unresolved_call_sites
                    WHERE source_file_id = @fileId;

                    DELETE FROM symbols
                    WHERE file_id = @fileId;

                    COMMIT;
                    """,
                    new Dictionary<string, object?> { ["fileId"] = fileId },
                    cancellationToken);
            });

    /// <inheritdoc />
    public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "delete-file",
            activity =>
            {
                activity?.SetTag("graphrag.file.id", fileId);

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    BEGIN;

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
                    """,
                    new Dictionary<string, object?> { ["fileId"] = fileId },
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

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    BEGIN;

                    UPDATE files
                    SET path = @path
                    WHERE id = @fileId;

                    COMMIT;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["fileId"] = fileId,
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

                List<string?> results = await this._postgreSqlRunner.QueryAsync(
                    """
                    SELECT indexed_commit
                    FROM repos
                    WHERE id = @repoId
                    LIMIT 1;
                    """,
                    reader => Task.FromResult(reader.IsDBNull(0) ? null : reader.GetString(0)),
                    new Dictionary<string, object?> { ["repoId"] = repoId },
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

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    UPDATE repos
                    SET indexed_commit = @indexedCommit,
                        indexed_at = NOW()
                    WHERE id = @repoId;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["repoId"] = repoId,
                        ["indexedCommit"] = indexedCommit,
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default)
    {
        ValidateVectorSearchArguments(queryText, topK);

        return this.RunOperationAsync<IReadOnlyList<VectorSearchResult>>(
            "vector-search-symbols",
            async activity =>
            {
                activity?.SetTag("graphrag.search.top_k", topK);
                activity?.SetTag("graphrag.search.type", "symbols");
                float[] queryEmbedding = (await this._embeddingGenerator.GenerateEmbeddingAsync(queryText, cancellationToken)).ToArray();

                List<VectorSearchResult> results = await this._postgreSqlRunner.QueryAsync(
                    """
                    SELECT id, (embedding <=> @queryEmbedding) AS distance
                    FROM symbols
                    WHERE embedding IS NOT NULL
                    ORDER BY embedding <=> @queryEmbedding
                    LIMIT @topK;
                    """,
                    reader => Task.FromResult(new VectorSearchResult
                    {
                        Id = reader.GetGuid(0),
                        Score = 1d - reader.GetDouble(1),
                    }),
                    new Dictionary<string, object?>
                    {
                        ["queryEmbedding"] = new NpgsqlParameter("queryEmbedding", new Vector(queryEmbedding)),
                        ["topK"] = topK,
                    },
                    cancellationToken);
                activity?.SetTag("graphrag.search.result_count", results.Count);
                return results;
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default)
    {
        ValidateVectorSearchArguments(queryText, topK);

        return this.RunOperationAsync<IReadOnlyList<VectorSearchResult>>(
            "vector-search-clusters",
            async activity =>
            {
                activity?.SetTag("graphrag.search.top_k", topK);
                activity?.SetTag("graphrag.search.type", "clusters");
                float[] queryEmbedding = (await this._embeddingGenerator.GenerateEmbeddingAsync(queryText, cancellationToken)).ToArray();

                List<VectorSearchResult> results = await this._postgreSqlRunner.QueryAsync(
                    """
                    SELECT id, (embedding <=> @queryEmbedding) AS distance
                    FROM clusters
                    WHERE embedding IS NOT NULL
                    ORDER BY embedding <=> @queryEmbedding
                    LIMIT @topK;
                    """,
                    reader => Task.FromResult(new VectorSearchResult
                    {
                        Id = reader.GetGuid(0),
                        Score = 1d - reader.GetDouble(1),
                    }),
                    new Dictionary<string, object?>
                    {
                        ["queryEmbedding"] = new NpgsqlParameter("queryEmbedding", new Vector(queryEmbedding)),
                        ["topK"] = topK,
                    },
                    cancellationToken);
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
                List<TraversalHop> hops = await this._postgreSqlRunner.QueryAsync(
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

                List<Symbol> results = await this._postgreSqlRunner.QueryAsync(
                    """
                    SELECT id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                           embedding, content_hash, is_utility, source_range_start, source_range_end
                    FROM symbols
                    WHERE id = @symbolId
                    LIMIT 1;
                    """,
                    reader => Task.FromResult(HydrateSymbol(reader)),
                    new Dictionary<string, object?> { ["symbolId"] = symbolId },
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

                List<Symbol> matches = await this._postgreSqlRunner.QueryAsync(
                    """
                    WITH ranked_matches AS (
                        SELECT id, 0 AS match_rank, 1.0::double precision AS match_score
                        FROM symbols
                        WHERE name = @name

                        UNION ALL

                        SELECT id, 1 AS match_rank, similarity(name, @name) AS match_score
                        FROM symbols
                        WHERE name % @name
                    ),
                    deduped_matches AS (
                        SELECT id,
                               MIN(match_rank) AS match_rank,
                               MAX(match_score) AS match_score
                        FROM ranked_matches
                        GROUP BY id
                    )
                    SELECT s.id, s.file_id, s.module_id, s.name, s.fully_qualified_name, s.kind, s.signature, s.summary, s.one_line_summary,
                           s.embedding, s.content_hash, s.is_utility, s.source_range_start, s.source_range_end
                    FROM deduped_matches AS m
                    JOIN symbols AS s
                      ON s.id = m.id
                    ORDER BY m.match_rank ASC, m.match_score DESC, s.name ASC, s.fully_qualified_name ASC;
                    """,
                    reader => Task.FromResult(HydrateSymbol(reader)),
                    new Dictionary<string, object?> { ["name"] = name },
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
            activity =>
            {
                activity?.SetTag("graphrag.call_site.count", callSites.Count);
                if (callSites.Count == 0)
                {
                    return Task.CompletedTask;
                }

                Guid[] ids = new Guid[callSites.Count];
                Guid[] sourceSymbolIds = new Guid[callSites.Count];
                Guid[] sourceFileIds = new Guid[callSites.Count];
                string[] identifiers = new string[callSites.Count];
                string?[] scopes = new string?[callSites.Count];
                string?[] llmExtractedTargets = new string?[callSites.Count];

                for (int index = 0; index < callSites.Count; index++)
                {
                    UnresolvedCallSite callSite = callSites[index];
                    ids[index] = callSite.Id;
                    sourceSymbolIds[index] = callSite.SourceSymbolId;
                    sourceFileIds[index] = callSite.SourceFileId;
                    identifiers[index] = callSite.Identifier;
                    scopes[index] = callSite.Scope;
                    llmExtractedTargets[index] = callSite.LlmExtractedTarget;
                }

                return this._postgreSqlRunner.ExecuteAsync(
                    """
                    INSERT INTO unresolved_call_sites (id, source_symbol_id, source_file_id, identifier, scope, llm_extracted_target)
                    SELECT *
                    FROM unnest(@ids, @sourceSymbolIds, @sourceFileIds, @identifiers, @scopes, @llmExtractedTargets)
                        AS call_sites(id, source_symbol_id, source_file_id, identifier, scope, llm_extracted_target)
                    ON CONFLICT (id) DO UPDATE
                    SET source_symbol_id = excluded.source_symbol_id,
                        source_file_id = excluded.source_file_id,
                        identifier = excluded.identifier,
                        scope = excluded.scope,
                        llm_extracted_target = excluded.llm_extracted_target;
                    """,
                    new Dictionary<string, object?>
                    {
                        ["ids"] = CreateArrayParameter("ids", NpgsqlDbType.Uuid, ids),
                        ["sourceSymbolIds"] = CreateArrayParameter("sourceSymbolIds", NpgsqlDbType.Uuid, sourceSymbolIds),
                        ["sourceFileIds"] = CreateArrayParameter("sourceFileIds", NpgsqlDbType.Uuid, sourceFileIds),
                        ["identifiers"] = CreateArrayParameter("identifiers", NpgsqlDbType.Text, identifiers),
                        ["scopes"] = CreateArrayParameter("scopes", NpgsqlDbType.Text, scopes),
                        ["llmExtractedTargets"] = CreateArrayParameter("llmExtractedTargets", NpgsqlDbType.Text, llmExtractedTargets),
                    },
                    cancellationToken);
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) =>
        this.RunOperationAsync(
            "drain-unresolved-call-sites",
            async activity =>
            {
                activity?.SetTag("graphrag.file.id", sourceFileId?.ToString("D", CultureInfo.InvariantCulture));

                List<UnresolvedCallSite> callSites = await this._postgreSqlRunner.QueryAsync(
                    """
                    WITH drained AS (
                        DELETE FROM unresolved_call_sites
                        WHERE @sourceFileId::uuid IS NULL OR source_file_id = @sourceFileId
                        RETURNING id, source_symbol_id, source_file_id, identifier, scope, llm_extracted_target
                    )
                    SELECT id, source_symbol_id, source_file_id, identifier, scope, llm_extracted_target
                    FROM drained
                    ORDER BY id ASC;
                    """,
                    reader => Task.FromResult(HydrateUnresolvedCallSite(reader)),
                    new Dictionary<string, object?> { ["sourceFileId"] = sourceFileId },
                    cancellationToken);

                activity?.SetTag("graphrag.call_site.result_count", callSites.Count);
                return (IReadOnlyList<UnresolvedCallSite>)callSites;
            });

    /// <inheritdoc />
    public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, ValueTuple<Guid, string>> assignments, CancellationToken cancellationToken = default)
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
                await using var connection = new NpgsqlConnection(this._connectionString);
                await connection.OpenAsync(cancellationToken);
                await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

                try
                {
                    await using (var deleteMembershipCommand = new NpgsqlCommand("DELETE FROM edges WHERE edge_kind = 'MemberOf';", connection, transaction))
                    {
                        await deleteMembershipCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await using (var deleteClustersCommand = new NpgsqlCommand("DELETE FROM clusters;", connection, transaction))
                    {
                        await deleteClustersCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    for (int index = 0; index < clusters.Count; index++)
                    {
                        Agency.GraphRAG.Code.Domain.Cluster cluster = clusters[index];
                        await using var insertCommand = new NpgsqlCommand(
                            """
                            INSERT INTO clusters (id, label, summary, embedding, coherence, type, level)
                            VALUES (@id, @label, @summary, @embedding::vector, @coherence, @type, @level);
                            """,
                            connection,
                            transaction);
                        insertCommand.Parameters.AddWithValue("id", cluster.Id);
                        insertCommand.Parameters.AddWithValue("label", cluster.Label);
                        insertCommand.Parameters.AddWithValue("summary", (object?)cluster.Summary ?? DBNull.Value);
                        insertCommand.Parameters.AddWithValue("embedding", (object?)FormatVectorLiteral(embeddings[index]) ?? DBNull.Value);
                        insertCommand.Parameters.AddWithValue("coherence", cluster.CoherenceScore);
                        insertCommand.Parameters.AddWithValue("type", cluster.Type.ToString());
                        insertCommand.Parameters.AddWithValue("level", 0);
                        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            });
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return this.RunOperationAsync(
            "get-symbols-by-paths",
            async activity =>
            {
                activity?.SetTag("graphrag.path.count", paths.Count);

                if (paths.Count == 0)
                {
                    return new Dictionary<string, IReadOnlyList<Symbol>>();
                }

                string[] pathsArray = new string[paths.Count];
                for (int index = 0; index < paths.Count; index++)
                {
                    pathsArray[index] = paths[index];
                }

                List<(Symbol Symbol, string Path)> rows = await this._postgreSqlRunner.QueryAsync(
                    """
                    SELECT s.id, s.file_id, s.module_id, s.name, s.fully_qualified_name, s.kind, s.signature, s.summary, s.one_line_summary,
                           s.embedding, s.content_hash, s.is_utility, s.source_range_start, s.source_range_end, f.path
                    FROM symbols s
                    JOIN files f ON s.file_id = f.id
                    WHERE f.path = ANY(@paths);
                    """,
                    reader => Task.FromResult((HydrateSymbol(reader), reader.GetString(14))),
                    new Dictionary<string, object?>
                    {
                        ["paths"] = CreateArrayParameter("paths", NpgsqlDbType.Text, pathsArray),
                    },
                    cancellationToken);

                var resultDict = new Dictionary<string, List<Symbol>>();
                foreach (var (symbol, path) in rows)
                {
                    if (!resultDict.ContainsKey(path))
                    {
                        resultDict[path] = [];
                    }

                    resultDict[path].Add(symbol);
                }

                activity?.SetTag("graphrag.symbol.result_count", rows.Count);

                Dictionary<string, IReadOnlyList<Symbol>> result = new();
                foreach (var (path, symbols) in resultDict)
                {
                    result[path] = symbols.AsReadOnly();
                }

                return (IReadOnlyDictionary<string, IReadOnlyList<Symbol>>)result;
            });
    }

    /// <inheritdoc />
    public Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be null or whitespace.", nameof(path));
        }

        return this.RunOperationAsync(
            "get-file-by-path",
            async activity =>
            {
                activity?.SetTag("graphrag.file.path", path);

                List<SourceFile> results = await this._postgreSqlRunner.QueryAsync(
                    """
                    SELECT id, repo_id, project_id, path, language, content_hash
                    FROM files
                    WHERE path = @path
                    LIMIT 1;
                    """,
                    reader => Task.FromResult(HydrateSourceFile(reader)),
                    new Dictionary<string, object?> { ["path"] = path },
                    cancellationToken);

                return results.Count == 0 ? null : results[0];
            });
    }

    private async Task RunOperationAsync(string operationName, Func<Activity?, Task> action)
    {
        using var activity = _activitySource.StartActivity($"graphrag.postgres.{operationName}", ActivityKind.Client);
        activity?.SetTag("graphrag.operation", operationName);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Starting PostgreSQL graph store operation {Operation}", operationName);

        try
        {
            await action(activity);
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", operationName }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", operationName } });
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Completed PostgreSQL graph store operation {Operation} in {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
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
            this._logger.LogError(ex, "PostgreSQL graph store operation {Operation} failed after {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private async Task<T> RunOperationAsync<T>(string operationName, Func<Activity?, Task<T>> action)
    {
        using var activity = _activitySource.StartActivity($"graphrag.postgres.{operationName}", ActivityKind.Client);
        activity?.SetTag("graphrag.operation", operationName);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Starting PostgreSQL graph store operation {Operation}", operationName);

        try
        {
            T result = await action(activity);
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", operationName }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", operationName } });
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Completed PostgreSQL graph store operation {Operation} in {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
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
            this._logger.LogError(ex, "PostgreSQL graph store operation {Operation} failed after {ElapsedMs}ms", operationName, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    private static Dictionary<string, object?> CreateSymbolParameters(Symbol symbol, int? index)
    {
        string suffix = index?.ToString() ?? string.Empty;
        return new Dictionary<string, object?>
        {
            ["id" + suffix] = symbol.Id,
            ["fileId" + suffix] = symbol.FileId,
            ["moduleId" + suffix] = symbol.ModuleId,
            ["name" + suffix] = symbol.Name,
            ["fullyQualifiedName" + suffix] = symbol.FullyQualifiedName,
            ["kind" + suffix] = symbol.Kind.ToString(),
            ["signature" + suffix] = symbol.Signature,
            ["summary" + suffix] = symbol.Summary,
            ["oneLineSummary" + suffix] = symbol.OneLineSummary,
            ["embedding" + suffix] = CreateVectorParameter("embedding" + suffix, symbol.Embedding),
            ["contentHash" + suffix] = symbol.ContentHash,
            ["isUtility" + suffix] = symbol.IsUtility,
            ["sourceRangeStart" + suffix] = symbol.SourceRangeStart,
            ["sourceRangeEnd" + suffix] = symbol.SourceRangeEnd,
        };
    }

    private static void AppendSymbolValues(StringBuilder sqlBuilder, Dictionary<string, object?> parameters, Symbol symbol, int index)
    {
        string suffix = index.ToString();
        if (index > 0)
        {
            _ = sqlBuilder.AppendLine(",");
        }

        _ = sqlBuilder.Append(
            $"""
                (@id{suffix}, @fileId{suffix}, @moduleId{suffix}, @name{suffix}, @fullyQualifiedName{suffix}, @kind{suffix}, @signature{suffix}, @summary{suffix}, @oneLineSummary{suffix},
                 @embedding{suffix}, @contentHash{suffix}, @isUtility{suffix}, @sourceRangeStart{suffix}, @sourceRangeEnd{suffix})
            """);

        foreach (var (key, value) in CreateSymbolParameters(symbol, index))
        {
            parameters[key] = value;
        }
    }

    private static NpgsqlParameter CreateArrayParameter<T>(string name, NpgsqlDbType elementType, T[] values) =>
        new(name, NpgsqlDbType.Array | elementType)
        {
            Value = values,
        };

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
            _ = seedsBuilder.Append("(@").Append(parameterName).Append("::uuid)");
            parameters[parameterName] = request.SeedSymbolIds[index];
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

        List<string> edgeExpansions = [];
        if (request.Direction is TraversalDirection.Outgoing or TraversalDirection.Both)
        {
            edgeExpansions.Add(
                $"""
                SELECT e.id AS edge_id,
                       e.target_id AS next_symbol_id
                FROM edges AS e
                WHERE e.source_id = t.symbol_id
                  AND e.source_kind = 'symbol'
                  AND e.target_kind = 'symbol'
                  AND e.confidence >= @minConfidence{edgeFilter}
                """);
        }

        if (request.Direction is TraversalDirection.Incoming or TraversalDirection.Both)
        {
            edgeExpansions.Add(
                $"""
                SELECT e.id AS edge_id,
                       e.source_id AS next_symbol_id
                FROM edges AS e
                WHERE e.target_id = t.symbol_id
                  AND e.source_kind = 'symbol'
                  AND e.target_kind = 'symbol'
                  AND e.confidence >= @minConfidence{edgeFilter}
                """);
        }

        string recursiveBranchSql =
            """
            SELECT next_edges.next_symbol_id AS symbol_id,
                   t.depth + 1 AS depth,
                   next_edges.edge_id AS via_edge_id,
                   t.path || next_edges.next_symbol_id AS path
            FROM traversal AS t
            JOIN LATERAL (
            """
            + Environment.NewLine
            + string.Join(Environment.NewLine + "UNION ALL" + Environment.NewLine, edgeExpansions)
            + Environment.NewLine
            + """
            ) AS next_edges
              ON TRUE
            WHERE t.depth < @maxHops
              AND NOT next_edges.next_symbol_id = ANY(t.path)
            """;
        return $"""
               WITH RECURSIVE seed_symbols(symbol_id) AS (
                   VALUES {seedsBuilder}
               ),
               traversal(symbol_id, depth, via_edge_id, path) AS (
                   SELECT symbol_id,
                          0 AS depth,
                          NULL::uuid AS via_edge_id,
                          ARRAY[symbol_id] AS path
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
                      e.signals::text,
                      e.properties::text
               FROM traversal
               LEFT JOIN edges AS e
                 ON e.id = traversal.via_edge_id
               ORDER BY traversal.depth ASC, traversal.symbol_id ASC, e.id ASC;
               """;
    }

    private static TraversalHop HydrateTraversalHop(DbDataReader reader) =>
        new()
        {
            SymbolId = reader.GetGuid(0),
            Depth = reader.GetInt32(1),
            ViaEdge = reader.IsDBNull(2) ? null : HydrateEdge(reader, 2),
        };

    private static Symbol HydrateSymbol(DbDataReader reader) =>
        new()
        {
            Id = reader.GetGuid(0),
            FileId = reader.GetGuid(1),
            ModuleId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
            Name = reader.GetString(3),
            FullyQualifiedName = reader.IsDBNull(4) ? null : reader.GetString(4),
            Kind = Enum.Parse<SymbolKind>(reader.GetString(5), ignoreCase: true),
            Signature = reader.IsDBNull(6) ? null : reader.GetString(6),
            Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
            OneLineSummary = reader.IsDBNull(8) ? null : reader.GetString(8),
            Embedding = reader.IsDBNull(9) ? null : DeserializeEmbedding(reader.GetValue(9)),
            ContentHash = reader.IsDBNull(10) ? null : reader.GetString(10),
            IsUtility = reader.GetBoolean(11),
            SourceRangeStart = reader.GetInt32(12),
            SourceRangeEnd = reader.GetInt32(13),
        };

    private static SourceFile HydrateSourceFile(DbDataReader reader) =>
        new()
        {
            Id = reader.GetGuid(0),
            RepoId = reader.GetGuid(1),
            ProjectId = reader.GetGuid(2),
            Path = reader.GetString(3),
            Language = reader.GetString(4),
            ContentHash = reader.IsDBNull(5) ? null : reader.GetString(5),
        };

    private static Edge HydrateEdge(DbDataReader reader, int offset) =>
        new()
        {
            Id = reader.GetGuid(offset),
            SourceId = reader.GetGuid(offset + 1),
            SourceKind = reader.GetString(offset + 2),
            TargetId = reader.GetGuid(offset + 3),
            TargetKind = reader.GetString(offset + 4),
            EdgeKind = Enum.Parse<EdgeKind>(reader.GetString(offset + 5), ignoreCase: true),
            Confidence = reader.GetDouble(offset + 6),
            Signals = DeserializeSignals(reader.GetString(offset + 7)),
            Properties = DeserializeProperties(reader.GetString(offset + 8)),
        };

    private static UnresolvedCallSite HydrateUnresolvedCallSite(DbDataReader reader) =>
        new()
        {
            Id = reader.GetGuid(0),
            SourceSymbolId = reader.GetGuid(1),
            SourceFileId = reader.GetGuid(2),
            Identifier = reader.GetString(3),
            Scope = reader.IsDBNull(4) ? null : reader.GetString(4),
            LlmExtractedTarget = reader.IsDBNull(5) ? null : reader.GetString(5),
        };

    private static object? CreateVectorParameter(string name, float[]? embedding) =>
        embedding is null ? DBNull.Value : CreateVectorDbParameter(name, embedding);

    private static NpgsqlParameter CreateVectorDbParameter(string name, float[]? embedding) =>
        embedding is null
            ? new NpgsqlParameter(name, DBNull.Value)
            : new NpgsqlParameter(name, new Vector(embedding));

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

    private static float[]? DeserializeEmbedding(object dbValue) =>
        dbValue switch
        {
            Vector vector => vector.Memory.ToArray(),
            string text => ParseVectorLiteral(text),
            _ => throw new InvalidOperationException($"Unsupported embedding value type '{dbValue.GetType().FullName}'."),
        };

    private static float[] ParseVectorLiteral(string raw) =>
        raw.Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => float.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();

    private static string? FormatVectorLiteral(float[]? embedding) =>
        embedding is null
            ? null
            : "[" + string.Join(",", embedding.Select(value => value.ToString("G9", CultureInfo.InvariantCulture))) + "]";

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

    private static Guid CreateDeterministicGuid(params string[] parts)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
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

    private static string GetConnectionString(PostgreSqlRunner postgreSqlRunner)
    {
        FieldInfo? field = typeof(PostgreSqlRunner).GetField("_dataSource", BindingFlags.Instance | BindingFlags.NonPublic);
        object? dataSource = field?.GetValue(postgreSqlRunner);
        string? connectionString = dataSource?.GetType().GetProperty("ConnectionString", BindingFlags.Instance | BindingFlags.Public)?.GetValue(dataSource) as string;
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        throw new InvalidOperationException("Unable to read the PostgreSQL connection string from the provided runner.");
    }
}
