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
            .AddSingleton<IChatOutput, ConsoleOutput>()
            .AddTransient<Models>()
            .AddTelemetry(configuration);

        services.AddOptions<AgentOptions>()
        .BindConfiguration("Agent");

        services.AddScoped<IAgentFactory, AgentFactory>();

        services.AddScoped(sp =>
        {
            IAgentFactory agentFactory = sp.GetRequiredService<IAgentFactory>();
            AgentOptions options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return agentFactory.CreateAgent(null, null, options.Stream);
        });

        services.AddScoped(sp =>
        {
            IAgentFactory agentFactory = sp.GetRequiredService<IAgentFactory>();
            AgentOptions options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;

            var registry = new ToolRegistry();
            registry.Register(new ExecutePowershellTool());
            registry.Register(new ReadFileTool());
            registry.Register(new WriteFileTool());
            registry.Register(new AgentTool((clientName, modelName, stream) => (options, agentFactory.CreateAgent(clientName, modelName, stream), registry)));

            return new ToolContext()
            {
                Registry = registry
            };
        });

        services.AddScoped<ConsoleChatSession>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConsoleChatSession app = scope.ServiceProvider.GetRequiredService<ConsoleChatSession>();
        await app.RunAsync();
        Log.CloseAndFlush();
    }
}