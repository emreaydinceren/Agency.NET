using FluentMigrator.Runner;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agency.GraphRAG.Code.Postgres.Migrations;

/// <summary>
/// Runs GraphRAG.Code PostgreSQL migrations to the latest version.
/// </summary>
public sealed class PostgresMigrationRunner
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresMigrationRunner"/> class.
    /// </summary>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    public PostgresMigrationRunner(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or whitespace.", nameof(connectionString));
        }

        this._connectionString = connectionString;
    }

    /// <summary>
    /// Applies all pending migrations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        string versionTableSchema = await ResolvePrimarySchemaAsync(this._connectionString, cancellationToken);

        using var serviceProvider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(builder => builder
                .AddPostgres()
                .WithGlobalConnectionString(this._connectionString)
                .ScanIn(typeof(PostgresMigrationRunner).Assembly).For.Migrations())
            .AddScoped<IVersionTableMetaData>(_ => new SchemaScopedVersionTableMetaData(versionTableSchema))
            .BuildServiceProvider(validateScopes: false);

        await using var scope = serviceProvider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        await Task.Run(() => runner.MigrateUp(), cancellationToken);
    }

    private static async Task<string> ResolvePrimarySchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        string? searchPath = builder.SearchPath;

        if (!string.IsNullOrWhiteSpace(searchPath))
        {
            string primarySchema = searchPath.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            return string.IsNullOrWhiteSpace(primarySchema) ? "public" : primarySchema;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_schema()";
        object? schema = await command.ExecuteScalarAsync(cancellationToken);
        string? resolvedSchema = schema?.ToString();

        return string.IsNullOrWhiteSpace(resolvedSchema) ? "public" : resolvedSchema;
    }

    [VersionTableMetaData]
    private sealed class SchemaScopedVersionTableMetaData(string schemaName) : IVersionTableMetaData
    {
        public bool OwnsSchema => false;

        public string SchemaName { get; } = schemaName;

        public string TableName => "VersionInfo";

        public string ColumnName => "Version";

        public string DescriptionColumnName => "Description";

        public string UniqueIndexName => "UC_Version";

        public string AppliedOnColumnName => "AppliedOn";

        public bool CreateWithPrimaryKey => false;
    }
}
