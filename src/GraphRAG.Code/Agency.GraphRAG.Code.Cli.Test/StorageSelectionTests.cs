using Agency.GraphRAG.Code.DependencyInjection;

namespace Agency.GraphRAG.Code.Cli.Test;

/// <summary>
/// Verifies CLI storage selection defaults and validation.
/// </summary>
public sealed class StorageSelectionTests
{
    [Fact]
    public void CreateIndexInvocation_WithoutStoreFlag_DefaultsToSqliteInWorkingDirectory()
    {
        CliInvocation invocation = CliApplication.CreateIndexInvocation(
            @"E:\Repos\Agency",
            "sqlite",
            null,
            null,
            null,
            null,
            @"E:\Repos\Agency");

        Assert.Equal(CodeIndexStore.Sqlite, invocation.Options.Store);
        Assert.Equal("Data Source=E:\\Repos\\Agency\\graphrag-code.db", invocation.Options.ConnectionString);
        Assert.Equal(@"E:\Repos\Agency\graphrag-code.db", invocation.Options.SqlitePath);
    }

    [Fact]
    public void CreateQueryInvocation_PostgresWithoutConnection_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CliApplication.CreateQueryInvocation(
            "find Agent",
            "postgres",
            null,
            5,
            null,
            null,
            null,
            @"E:\Repos\Agency"));

        Assert.Equal("Postgres store requires a connection string.", exception.Message);
    }
}
