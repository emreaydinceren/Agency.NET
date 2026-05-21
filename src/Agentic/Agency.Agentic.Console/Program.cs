global using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Agentic.Console.Test")]

namespace Agency.Agentic.Console;

using Agency.Agentic;
using Agency.Agentic.Console.Telemetry;
using Agency.Agentic.Contexts;
using Agency.Agentic.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

internal class Program
{
    public static async Task Main(string[] args)
    {
        // Encoding and Log initialization remain at the very top
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.InputEncoding = System.Text.Encoding.UTF8;

        var builder = Host.CreateApplicationBuilder(args);

        // 1. Configuration:
        // Host.CreateApplicationBuilder automatically handles appsettings, 
        // environment variables, and UserSecrets based on DOTNET_ENVIRONMENT.

        // 2. Telemetry & Logging:
        builder.Services.AddTelemetry(builder.Configuration);

        // 3. Simple Registrations:
        builder.Services.AddSingleton<IChatOutput, ConsoleOutput>();
        builder.Services.AddTransient<Models>();

        // 4. Options Pattern:
        builder.Services.AddOptions<AgentOptions>()
            .BindConfiguration("Agent")
            .ValidateOnStart();

        // 5. Factory & Agent Registration:
        builder.Services.AddScoped<IAgentFactory, AgentFactory>();

        builder.Services.AddScoped(sp =>
        {
            var agentFactory = sp.GetRequiredService<IAgentFactory>();
            var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            return agentFactory.CreateAgent(null, null);
        });

        // 6. Tool & Context Registration:
        builder.Services.AddScoped(sp =>
        {
            var agentFactory = sp.GetRequiredService<IAgentFactory>();
            var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;

            var registry = new ToolRegistry();
            registry.Register(new ExecutePowershellTool());
            registry.Register(new ReadFileTool());
            registry.Register(new WriteFileTool());

            registry.Register(new AgentTool((clientName, modelName) =>
                (options, agentFactory.CreateAgent(clientName, modelName), registry)));

            return new ToolContext { Registry = registry };
        });

        builder.Services.AddScoped<ConsoleChatSession>();

        // 7. Execution:
        using IHost host = builder.Build();

        using (var scope = host.Services.CreateScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<ConsoleChatSession>();
            await app.RunAsync();
        }

        // Serilog/Log cleanup
        await Log.CloseAndFlushAsync();
    }
}