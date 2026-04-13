global using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agency.Agentic.Console.Test")]

namespace Agency.Agentic.Console;

using Agency.Agentic;
using Agency.Llm.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal class Program
{
    public static async Task Main()
    {
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();

        services
            .AddSingleton<IConfiguration>(configuration)
            .AddTransient<Models>();

        services.AddOptions<AgentOptions>()
        .BindConfiguration("Agent");

        services.AddSingleton(sp =>
        {
            var models = sp.GetRequiredService<Models>();
            return models.CreateLlmClient();
        });

        services.AddSingleton(sp =>
        {
            AgentOptions options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
            string model = options.DefaultModel
                ?? throw new InvalidOperationException(
                    "Missing required configuration value 'Agent:Model'.");
            ILlmClient llmClient = sp.GetRequiredService<ILlmClient>();
            return new Agent(llmClient, model, stream: options.Stream);
        });

        services.AddSingleton<ConsoleChatSession>();

        using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var app = serviceProvider.GetRequiredService<ConsoleChatSession>();
        await app.RunAsync();
    }
}