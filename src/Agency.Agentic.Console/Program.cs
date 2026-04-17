global using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Agentic.Console.Test")]

namespace Agency.Agentic.Console;

using Agency.Agentic;
using Agency.Agentic.Console.Telemetry;
using Agency.Agentic.Tools;
using Agency.Agentic.Contexts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

internal class Program
{
    private static ServiceProvider? serviceProvider;

    public static async Task Main()
    {
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.InputEncoding = System.Text.Encoding.UTF8;

        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        ServiceCollection services = new ServiceCollection();

        services
            .AddSingleton<IConfiguration>(configuration)
            .AddTransient<Models>()
            .AddTelemetry(configuration);

        services.AddOptions<AgentOptions>()
        .BindConfiguration("Agent");

        services.AddSingleton(sp =>
        {
            AgentOptions options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return CreateAgent(null, null, options.Stream);
        });

        services.AddSingleton(sp => {
            AgentOptions options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;

            var registry = new ToolRegistry();
            registry.Register(new ExecutePowershellTool());
            registry.Register(new ReadFileTool());
            registry.Register(new WriteFileTool());
            registry.Register(new AgentTool((clientName, modelName, stream) => (options, CreateAgent(clientName, modelName, stream), registry)));

            return new ToolContext()
            {
                Registry = registry
            }; 
        });

        services.AddSingleton<ConsoleChatSession>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        serviceProvider = provider;
        ConsoleChatSession app = provider.GetRequiredService<ConsoleChatSession>();
        await app.RunAsync();
        Log.CloseAndFlush();
    }

    internal static Agent CreateAgent(
        string? clientName,
        string? modelName,
        bool stream)
    {
        if (serviceProvider is null)
        {
            throw new InvalidOperationException("Service provider is not initialized.");
        }

        AgentOptions options = serviceProvider.GetRequiredService<IOptions<AgentOptions>>().Value;
        clientName = !string.IsNullOrEmpty(clientName) ? clientName : (options.DefaultClientName ?? throw new InvalidOperationException("DefaultClientName must be specified in the configuration."));
        modelName = !string.IsNullOrEmpty(modelName) ? modelName : (options.DefaultModel ?? throw new InvalidOperationException("DefaultModel must be specified in the configuration."));

        var models = serviceProvider.GetRequiredService<Models>();
        var logger = serviceProvider.GetRequiredService<ILogger<Agent>>();
        var llmClient=  models.CreateLlmClient(clientName);
        return new Agent(llmClient, modelName,null, stream, logger);
    }
}