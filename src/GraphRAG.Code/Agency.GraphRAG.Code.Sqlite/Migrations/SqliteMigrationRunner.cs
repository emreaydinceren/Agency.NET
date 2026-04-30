using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Agency.GraphRAG.Code.Sqlite.Migrations;

/// <summary>
/// Runs FluentMigrator migrations for the SQLite GraphRAG schema.
/// </summary>
public sealed class SqliteMigrationRunner
{
    private static readonly AsyncLocal<MigrationContext?> _currentContext = new();

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteMigrationRunner"/> class.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string to migrate.</param>
    public SqliteMigrationRunner(string connectionString)
    {
        this._connectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string cannot be null or whitespace.", nameof(connectionString));
    }

    /// <summary>
    /// Gets the active migration context for the current async flow.
    /// </summary>
    internal static MigrationContext CurrentContext => _currentContext.Value ?? new MigrationContext();

    /// <summary>
    /// Applies all pending migrations to the target SQLite database.
    /// </summary>
    /// <param name="context">Optional migration-time settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public Task MigrateToLatestAsync(MigrationContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddSQLite()
                .WithGlobalConnectionString(this._connectionString)
                .ScanIn(typeof(SqliteMigrationRunner).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        _currentContext.Value = context ?? new MigrationContext();

        try
        {
            services.GetRequiredService<IMigrationRunner>().MigrateUp();
            return Task.CompletedTask;
        }
        finally
        {
            _currentContext.Value = null;
        }
    }

    /// <summary>
    /// Configures a SQLite connection for GraphRAG migrations and runtime use.
    /// </summary>
    /// <param name="connection">The connection to configure.</param>
    public static void ConfigureConnection(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        TryLoadSqliteVecExtension(connection);
    }

    private static void TryLoadSqliteVecExtension(SqliteConnection connection)
    {
        if (!TryEnableExtensions(connection))
        {
            return;
        }

        foreach (string candidate in GetExtensionCandidates())
        {
            try
            {
                connection.LoadExtension(candidate);
                return;
            }
            catch (SqliteException)
            {
            }
            catch (FileNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    private static bool TryEnableExtensions(SqliteConnection connection)
    {
        MethodInfo? method = typeof(SqliteConnection).GetMethod("EnableExtensions", BindingFlags.Instance | BindingFlags.Public);
        if (method is null)
        {
            return false;
        }

        method.Invoke(connection, [true]);
        return true;
    }

    private static IEnumerable<string> GetExtensionCandidates()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var fileNames = new[]
        {
            "vec0",
            "sqlite_vec",
            "sqlite-vec",
            "vec0.dll",
            "sqlite_vec.dll",
            "sqlite-vec.dll",
        };

        foreach (string fileName in fileNames)
        {
            yield return fileName;
        }

        if (!Directory.Exists(baseDirectory))
        {
            yield break;
        }

        foreach (string fileName in fileNames)
        {
            string directPath = Path.Combine(baseDirectory, fileName);
            yield return directPath;
        }

        foreach (string filePath in Directory.EnumerateFiles(baseDirectory, "*vec*.dll", SearchOption.AllDirectories))
        {
            yield return filePath;
        }
    }
}
