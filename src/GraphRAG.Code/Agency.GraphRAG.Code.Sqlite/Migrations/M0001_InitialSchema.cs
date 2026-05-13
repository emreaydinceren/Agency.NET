using FluentMigrator;
using Microsoft.Data.Sqlite;

namespace Agency.GraphRAG.Code.Sqlite.Migrations;

/// <summary>
/// Creates the full SQLite schema for GraphRAG code indexing, including FTS5 and vec0 virtual tables.
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
                ecosystem TEXT NULL,
                FOREIGN KEY (repo_id) REFERENCES repos(id) ON DELETE CASCADE
            );
            CREATE INDEX idx_projects_repo_id ON projects(repo_id);

            CREATE TABLE external_packages (
                id TEXT NOT NULL PRIMARY KEY,
                project_id TEXT NOT NULL,
                name TEXT NOT NULL,
                version TEXT NULL,
                version_resolved TEXT NULL,
                ecosystem TEXT NOT NULL,
                scope TEXT NOT NULL,
                FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
            );
            CREATE INDEX idx_external_packages_project_id ON external_packages(project_id);

            CREATE TABLE files (
                id TEXT NOT NULL PRIMARY KEY,
                repo_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                content_hash TEXT NULL,
                last_indexed_at TEXT NULL,
                FOREIGN KEY (repo_id) REFERENCES repos(id) ON DELETE CASCADE,
                FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX idx_files_repo_id_path ON files(repo_id, path);
            CREATE INDEX idx_files_project_id ON files(project_id);

            CREATE TABLE modules (
                id TEXT NOT NULL PRIMARY KEY,
                file_id TEXT NOT NULL,
                project_id TEXT NULL,
                name TEXT NOT NULL,
                path TEXT NULL,
                kind TEXT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
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
                source_range_end INTEGER NOT NULL,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE,
                FOREIGN KEY (module_id) REFERENCES modules(id) ON DELETE SET NULL
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
                llm_extracted_target TEXT NULL,
                FOREIGN KEY (source_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
                FOREIGN KEY (source_file_id) REFERENCES files(id) ON DELETE CASCADE
            );
            CREATE INDEX idx_unresolved_call_sites_source_file_id ON unresolved_call_sites(source_file_id);

            CREATE VIRTUAL TABLE symbols_fts USING fts5(
                name,
                content='symbols',
                content_rowid='rowid'
            );

            INSERT INTO symbols_fts(rowid, name)
            SELECT rowid, name
            FROM symbols;

            CREATE TRIGGER trg_symbols_ai_fts
            AFTER INSERT ON symbols
            BEGIN
                INSERT INTO symbols_fts(rowid, name)
                VALUES (new.rowid, new.name);
            END;

            CREATE TRIGGER trg_symbols_au_fts
            AFTER UPDATE ON symbols
            BEGIN
                INSERT INTO symbols_fts(symbols_fts, rowid, name)
                VALUES ('delete', old.rowid, old.name);
                INSERT INTO symbols_fts(rowid, name)
                VALUES (new.rowid, new.name);
            END;

            CREATE TRIGGER trg_symbols_ad_fts
            AFTER DELETE ON symbols
            BEGIN
                INSERT INTO symbols_fts(symbols_fts, rowid, name)
                VALUES ('delete', old.rowid, old.name);
            END;
            """);

        this.Execute.WithConnection((connection, _) =>
        {
            var sqliteConnection = (SqliteConnection)connection;
            SqliteMigrationRunner.ConfigureConnection(sqliteConnection);

            using var command = sqliteConnection.CreateCommand();
            command.CommandText =
                $"""
                CREATE VIRTUAL TABLE symbols_vec USING vec0(
                    symbol_id TEXT PRIMARY KEY,
                    embedding FLOAT[{SqliteMigrationRunner.CurrentContext.EmbeddingDimensions}]
                );

                CREATE VIRTUAL TABLE clusters_vec USING vec0(
                    cluster_id TEXT PRIMARY KEY,
                    embedding FLOAT[{SqliteMigrationRunner.CurrentContext.EmbeddingDimensions}]
                );
                """;
            command.ExecuteNonQuery();
        });
    }

    /// <inheritdoc />
    public override void Down()
    {
        this.Execute.WithConnection((connection, _) =>
        {
            var sqliteConnection = (SqliteConnection)connection;
            SqliteMigrationRunner.ConfigureConnection(sqliteConnection);

            using var command = sqliteConnection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE IF EXISTS symbols_vec;
                DROP TABLE IF EXISTS clusters_vec;
                """;
            command.ExecuteNonQuery();
        });

        this.Execute.Sql(
            """
            DROP TRIGGER IF EXISTS trg_symbols_ad_fts;
            DROP TRIGGER IF EXISTS trg_symbols_au_fts;
            DROP TRIGGER IF EXISTS trg_symbols_ai_fts;
            DROP TABLE IF EXISTS symbols_fts;
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
