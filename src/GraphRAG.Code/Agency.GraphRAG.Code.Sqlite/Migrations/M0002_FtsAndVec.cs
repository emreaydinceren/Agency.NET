using FluentMigrator;
using Microsoft.Data.Sqlite;

namespace Agency.GraphRAG.Code.Sqlite.Migrations;

/// <summary>
/// Adds FTS5 and sqlite-vec virtual tables plus synchronization triggers.
/// </summary>
[Migration(2)]
public sealed class M0002_FtsAndVec : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        this.Execute.Sql(
            """
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
        this.Execute.Sql(
            """
            DROP TRIGGER IF EXISTS trg_symbols_ad_fts;
            DROP TRIGGER IF EXISTS trg_symbols_au_fts;
            DROP TRIGGER IF EXISTS trg_symbols_ai_fts;
            DROP TABLE IF EXISTS symbols_fts;
            """);

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
    }
}
