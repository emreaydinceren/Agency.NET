using Agency.Sql.Postgre;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Agency.VectorStore.Sql.Postgre;
using Agency.VectorStore.Sql.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;

namespace Agency.Mcp.Memory;

internal class Program
{
    private async static void Main(string[] args)
    {
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.InputEncoding = System.Text.Encoding.UTF8;

        var builder = Host.CreateApplicationBuilder(args);

        // Ensure options are registered first!
        builder.Services.Configure<MemoryOptions>(builder.Configuration.GetSection("Memory"));

        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        builder.Services.AddKVStore();

        await builder.Build().RunAsync();
    }
}

[McpServerToolType]
public static class MemoryTool
{
    [McpServerTool, Description("Memorizes a provided piece of information.")]
    public static string Memorize(MemoryRecord record)
    {

    }

    [McpServerTool, Description("Deletes a memorized piece of information.")]
    public static string Forget(MemoryScope scope, string domain, string key)
    {

    }

    [McpServerTool, Description("Recalls the  memorized piece of information based on filter parameters..")]
    public static string Recall(MemoryScope scope, string? domain, string? key, string[]? tags)
    {

    }
}

public record class MemoryRecord
{
    public MemoryScope? Scope { set; get; }

    public string? Key { set; get; }

    public string? Domain { set; get; }

    public string? Value { set; get; }

    public string[]? Tags { set; get; }
}

public record class MemoryScope
{
    public string UserId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;
}

public class MemoryOptions
{
    /// <summary>
    /// Sets the provider to use for memory operations. This can be Sqlite or Postgres other
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// Sets the connection string to use for memory operations. The format of the connection string will depend on the
    /// provider specified. For Sqlite, it should be in the format "Data Source=path_to_db". For Postgres, it should be
    /// in the format "Host=myserver;Database=mydb;Username=myuser;Password=mypassword".
    /// </summary>
    public string ConnectionString {  get; set; } = string.Empty;
}

