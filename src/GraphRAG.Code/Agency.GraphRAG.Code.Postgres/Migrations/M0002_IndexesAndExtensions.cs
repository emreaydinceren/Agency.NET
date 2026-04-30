using FluentMigrator;

namespace Agency.GraphRAG.Code.Postgres.Migrations;

/// <summary>
/// Adds PostgreSQL extensions and retrieval-oriented indexes for GraphRAG.Code.
/// </summary>
[Migration(1_000_002)]
public sealed class M0002_IndexesAndExtensions : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        this.Execute.Sql("CREATE EXTENSION IF NOT EXISTS vector");
        this.Execute.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm");

        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_symbols_file_id ON symbols (file_id);");
        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_symbols_module_id ON symbols (module_id);");
        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_symbols_name ON symbols (name);");
        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_symbols_name_trgm ON symbols USING gin (name gin_trgm_ops);");
        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_symbols_embedding_hnsw ON symbols USING hnsw (embedding vector_cosine_ops);");

        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_clusters_embedding_hnsw ON clusters USING hnsw (embedding vector_cosine_ops);");

        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_edges_source_id_edge_kind ON edges (source_id, edge_kind);");
        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_edges_target_id_edge_kind ON edges (target_id, edge_kind);");
        this.Execute.Sql("CREATE INDEX IF NOT EXISTS ix_edges_edge_kind_confidence ON edges (edge_kind, confidence);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        this.Execute.Sql("""
            DROP INDEX IF EXISTS ix_edges_edge_kind_confidence;
            DROP INDEX IF EXISTS ix_edges_target_id_edge_kind;
            DROP INDEX IF EXISTS ix_edges_source_id_edge_kind;
            DROP INDEX IF EXISTS ix_clusters_embedding_hnsw;
            DROP INDEX IF EXISTS ix_symbols_embedding_hnsw;
            DROP INDEX IF EXISTS ix_symbols_name_trgm;
            DROP INDEX IF EXISTS ix_symbols_name;
            DROP INDEX IF EXISTS ix_symbols_module_id;
            DROP INDEX IF EXISTS ix_symbols_file_id;
            """);
    }
}
