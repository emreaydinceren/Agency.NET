using FluentMigrator;

namespace Agency.GraphRAG.Code.Sqlite.Migrations;

/// <summary>
/// Creates the base SQLite schema for GraphRAG code indexing.
/// </summary>
[Migration(1)]
public sealed class M0001_InitialSchema : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        this.Execute.Sql(
            """
            CREATE TABLE repos (
                id TEXT NOT NULL PRIMARY KEY,
                remote_url TEXT NULL,
                root_path TEXT NOT NULL,
                indexed_commit TEXT NULL,
                indexed_at TEXT NULL,
                is_shallow INTEGER NOT NULL
            );

            CREATE TABLE projects (
                id TEXT NOT NULL PRIMARY KEY,
                repo_id TEXT NOT NULL,
                name TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                manifest_path TEXT NULL,
                language TEXT NOT NULL,
                ecosystem TEXT NULL
            );
            CREATE INDEX idx_projects_repo_id ON projects(repo_id);

            CREATE TABLE external_packages (
                id TEXT NOT NULL PRIMARY KEY,
                project_id TEXT NOT NULL,
                name TEXT NOT NULL,
                version TEXT NULL,
                version_resolved TEXT NULL,
                ecosystem TEXT NOT NULL,
                scope TEXT NOT NULL
            );
            CREATE INDEX idx_external_packages_project_id ON external_packages(project_id);

            CREATE TABLE files (
                id TEXT NOT NULL PRIMARY KEY,
                repo_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                content_hash TEXT NULL,
                last_indexed_at TEXT NULL
            );
            CREATE UNIQUE INDEX idx_files_repo_id_path ON files(repo_id, path);
            CREATE INDEX idx_files_project_id ON files(project_id);

            CREATE TABLE modules (
                id TEXT NOT NULL PRIMARY KEY,
                file_id TEXT NOT NULL,
                project_id TEXT NULL,
                name TEXT NOT NULL,
                path TEXT NULL,
                kind TEXT NULL
            );
            CREATE INDEX idx_modules_file_id ON modules(file_id);

            CREATE TABLE symbols (
                id TEXT NOT NULL PRIMARY KEY,
                file_id TEXT NOT NULL,
                module_id TEXT NULL,
                name TEXT NOT NULL,
                fully_qualified_name TEXT NULL,
                kind TEXT NOT NULL,
                signature TEXT NULL,
                summary TEXT NULL,
                one_line_summary TEXT NULL,
                embedding BLOB NULL,
                content_hash TEXT NULL,
                is_utility INTEGER NOT NULL,
                source_range_start INTEGER NOT NULL,
                source_range_end INTEGER NOT NULL
            );
            CREATE INDEX idx_symbols_file_id ON symbols(file_id);
            CREATE INDEX idx_symbols_module_id ON symbols(module_id);
            CREATE INDEX idx_symbols_name ON symbols(name);

            CREATE TABLE edges (
                id TEXT NOT NULL PRIMARY KEY,
                source_id TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                target_id TEXT NOT NULL,
                target_kind TEXT NOT NULL,
                edge_kind TEXT NOT NULL,
                confidence REAL NOT NULL,
                signals TEXT NOT NULL,
                properties TEXT NOT NULL
            );
            CREATE INDEX idx_edges_source_id_edge_kind ON edges(source_id, edge_kind);
            CREATE INDEX idx_edges_target_id_edge_kind ON edges(target_id, edge_kind);
            CREATE INDEX idx_edges_edge_kind_confidence ON edges(edge_kind, confidence);

            CREATE TABLE clusters (
                id TEXT NOT NULL PRIMARY KEY,
                label TEXT NULL,
                summary TEXT NULL,
                embedding BLOB NULL,
                coherence REAL NOT NULL,
                type TEXT NOT NULL,
                level INTEGER NOT NULL
            );

            CREATE TABLE unresolved_call_sites (
                id TEXT NOT NULL PRIMARY KEY,
                source_symbol_id TEXT NOT NULL,
                source_file_id TEXT NOT NULL,
                identifier TEXT NOT NULL,
                scope TEXT NULL,
                llm_extracted_target TEXT NULL
            );
            CREATE INDEX idx_unresolved_call_sites_source_file_id ON unresolved_call_sites(source_file_id);
            """);
    }

    /// <inheritdoc />
    public override void Down()
    {
        this.Execute.Sql(
            """
            DROP TABLE IF EXISTS unresolved_call_sites;
            DROP TABLE IF EXISTS clusters;
            DROP TABLE IF EXISTS edges;
            DROP TABLE IF EXISTS symbols;
            DROP TABLE IF EXISTS modules;
            DROP TABLE IF EXISTS files;
            DROP TABLE IF EXISTS external_packages;
            DROP TABLE IF EXISTS projects;
            DROP TABLE IF EXISTS repos;
            """);
    }
}
