using FluentMigrator;

namespace Agency.GraphRAG.Code.Postgres.Migrations;

/// <summary>
/// Creates the initial GraphRAG.Code PostgreSQL schema.
/// </summary>
[Migration(1_000_001)]
public sealed class M0001_InitialSchema : Migration
{
    internal const int DefaultEmbeddingDimensions = 1536;

    /// <inheritdoc />
    public override void Up()
    {
        this.Execute.Sql("CREATE EXTENSION IF NOT EXISTS vector");

        this.Execute.Sql($"""
            CREATE TABLE IF NOT EXISTS repos (
                id UUID PRIMARY KEY,
                remote_url TEXT NULL,
                root_path TEXT NOT NULL,
                indexed_commit TEXT NULL,
                indexed_at TIMESTAMPTZ NULL,
                is_shallow BOOLEAN NOT NULL
            );
            """);

        this.Execute.Sql("""
            CREATE TABLE IF NOT EXISTS projects (
                id UUID PRIMARY KEY,
                repo_id UUID NOT NULL,
                name TEXT NOT NULL,
                manifest_path TEXT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                ecosystem TEXT NULL
            );
            """);

        this.Execute.Sql("""
            CREATE TABLE IF NOT EXISTS external_packages (
                id UUID PRIMARY KEY,
                project_id UUID NOT NULL,
                name TEXT NOT NULL,
                version TEXT NULL,
                version_resolved TEXT NULL,
                ecosystem TEXT NOT NULL,
                scope TEXT NOT NULL
            );
            """);

        this.Execute.Sql("""
            CREATE TABLE IF NOT EXISTS files (
                id UUID PRIMARY KEY,
                repo_id UUID NOT NULL,
                project_id UUID NOT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                content_hash TEXT NULL,
                last_indexed_at TIMESTAMPTZ NULL
            );
            """);

        this.Execute.Sql("""
            CREATE TABLE IF NOT EXISTS modules (
                id UUID PRIMARY KEY,
                project_id UUID NOT NULL,
                file_id UUID NULL,
                name TEXT NOT NULL,
                path TEXT NULL,
                kind TEXT NULL
            );
            """);

        this.Execute.Sql($"""
            CREATE TABLE IF NOT EXISTS symbols (
                id UUID PRIMARY KEY,
                file_id UUID NOT NULL,
                module_id UUID NULL,
                name TEXT NOT NULL,
                fully_qualified_name TEXT NULL,
                kind TEXT NOT NULL,
                signature TEXT NULL,
                summary TEXT NULL,
                one_line_summary TEXT NULL,
                embedding vector({DefaultEmbeddingDimensions}) NULL,
                content_hash TEXT NULL,
                is_utility BOOLEAN NOT NULL,
                source_range_start INTEGER NOT NULL,
                source_range_end INTEGER NOT NULL
            );
            """);

        this.Execute.Sql($"""
            CREATE TABLE IF NOT EXISTS clusters (
                id UUID PRIMARY KEY,
                label TEXT NOT NULL,
                summary TEXT NULL,
                embedding vector({DefaultEmbeddingDimensions}) NULL,
                coherence REAL NOT NULL,
                type TEXT NOT NULL,
                level INTEGER NULL
            );
            """);

        this.Execute.Sql("""
            CREATE TABLE IF NOT EXISTS edges (
                id UUID PRIMARY KEY,
                source_id UUID NOT NULL,
                source_kind TEXT NOT NULL,
                target_id UUID NOT NULL,
                target_kind TEXT NOT NULL,
                edge_kind TEXT NOT NULL,
                confidence REAL NOT NULL,
                signals JSONB NOT NULL DEFAULT '[]'::jsonb,
                properties JSONB NOT NULL DEFAULT '{}'::jsonb
            );
            """);

        this.Execute.Sql("""
            CREATE TABLE IF NOT EXISTS unresolved_call_sites (
                id UUID PRIMARY KEY,
                source_symbol_id UUID NOT NULL,
                source_file_id UUID NOT NULL,
                identifier TEXT NOT NULL,
                scope TEXT NULL,
                llm_extracted_target TEXT NULL
            );
            """);
    }

    /// <inheritdoc />
    public override void Down()
    {
        this.Execute.Sql("""
            DROP TABLE IF EXISTS unresolved_call_sites;
            DROP TABLE IF EXISTS edges;
            DROP TABLE IF EXISTS clusters;
            DROP TABLE IF EXISTS symbols;
            DROP TABLE IF EXISTS modules;
            DROP TABLE IF EXISTS files;
            DROP TABLE IF EXISTS external_packages;
            DROP TABLE IF EXISTS projects;
            DROP TABLE IF EXISTS repos;
            """);
    }
}
