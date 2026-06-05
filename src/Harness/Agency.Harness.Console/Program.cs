global using System.Runtime.CompilerServices;

using Agency.Embeddings.OpenAI;
using Agency.Harness;
using Agency.Harness.Console.Telemetry;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Tools;
using Agency.Memory.Consolidator.DependencyInjection;
using Agency.Memory.Distiller;
using Agency.Memory.Distiller.DependencyInjection;
using Agency.Memory.Hygiene.DependencyInjection;
using Agency.Memory.Common.Storage;
using Agency.Memory.Sql.Postgres;
using Agency.Memory.Sql.Sqlite;
using Agency.Llm.OpenAI;
using Agency.Llm.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

[assembly: InternalsVisibleTo("Agency.Harness.Console.Test")]

namespace Agency.Harness.Console;
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

        builder.Services.AddAgencyConfiguredHooks(builder.Configuration);

        // 5. Memory — opt-in via Memory:Enabled (default false).
        //    When disabled, NONE of the memory services are registered and the console
        //    behaves exactly as before. When enabled, Postgres + LM Studio embeddings must
        //    be reachable; schema init at startup will fail fast with a clear message if not.
        bool memoryEnabled = builder.Configuration.GetValue<bool>("Memory:Enabled");
        int embeddingDimensions = 1024;

        if (memoryEnabled)
        {
            // Provider selection: "postgres" (default) or "sqlite".
            string provider = builder.Configuration.GetValue<string>("Memory:Provider") ?? "postgres";

            embeddingDimensions = builder.Configuration.GetValue<int>("Embedding:Dimensions", 1024);

            // 5a. Embeddings
            builder.Services.AddAgencyEmbeddingsOpenAI(opts =>
            {
                builder.Configuration.GetSection(EmbeddingOptions.SectionName).Bind(opts);
            });

            // 5b. Memory store + repositories + schema initializer — provider chosen via Memory:Provider.
            switch (provider.ToLowerInvariant())
            {
                case "postgres":
                    string postgresConnectionString = builder.Configuration.GetConnectionString("PostgreSql")
                        ?? throw new InvalidOperationException(
                            "Memory provider is 'postgres' but ConnectionStrings:PostgreSql is not configured.");
                    builder.Services.AddAgencyMemoryPostgres(postgresConnectionString);
                    break;
                case "sqlite":
                    string sqliteConnectionString = builder.Configuration.GetConnectionString("Sqlite")
                        ?? throw new InvalidOperationException(
                            "Memory provider is 'sqlite' but ConnectionStrings:Sqlite is not configured.");
                    builder.Services.AddAgencyMemorySqlite(sqliteConnectionString);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported Memory:Provider '{provider}'. Expected 'postgres' or 'sqlite'.");
            }

            // 5c. IChatClient for the consolidator — reuse the default agent LLM client.
            //     The distiller gets its own dedicated IChatClient (registered below) so we
            //     resolve and build both here from the same agent configuration.
            string defaultClientName = builder.Configuration["Agent:DefaultClientName"]
                ?? throw new InvalidOperationException("Agent:DefaultClientName is not configured.");
            string defaultModel = builder.Configuration["Agent:DefaultModel"]
                ?? throw new InvalidOperationException("Agent:DefaultModel is not configured.");

            // Find the matching LlmClientOptions from configuration.
            LlmClientOptions[] llmClients = builder.Configuration
                .GetSection("Agent:LLmClients")
                .Get<LlmClientOptions[]>() ?? [];

            LlmClientOptions defaultClientOpts = Array.Find(
                llmClients,
                c => c.Name.Equals(defaultClientName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"No LLM client configuration found with name '{defaultClientName}'.");

            // Consolidator IChatClient (shared via singleton)
            Microsoft.Extensions.AI.IChatClient consolidatorClient =
                new OpenAIClient(defaultClientOpts).CreateChatClient();

            builder.Services.AddSingleton(consolidatorClient);

            // 5d. Distiller adapter — separate client instance with thinking suppressed.
            LlmClientOptions distillerClientOpts = defaultClientOpts with { SuppressThinking = true };
            Microsoft.Extensions.AI.IChatClient distillerClient =
                new OpenAIClient(distillerClientOpts).CreateChatClient();

            builder.Services.AddAgencyDistillerLlm(distillerClient, defaultModel);

            // 5e. Core memory services (event bus, distiller background service,
            //     inactivity timer, conversation registry, baseline AgentHooks singleton)
            builder.Services.AddAgencyMemory();

            // 5f. Consolidator background service
            builder.Services.AddAgencyConsolidator(opts =>
            {
                opts.Model = defaultModel;
            });

            // 5g. Hygiene sweeper background service
            builder.Services.AddAgencyHygiene();
        }

        // 6. Factory & Agent Registration:
        builder.Services.AddScoped<IAgentFactory, AgentFactory>();

        builder.Services.AddScoped(sp =>
        {
            var agentFactory = sp.GetRequiredService<IAgentFactory>();
            return agentFactory.CreateAgent(null, null);
        });

        // 7. Tool & Context Registration:
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

        // 8. Execution:
        using IHost host = builder.Build();

        // 8a. Schema init — only when memory is enabled; fail fast if Postgres is unreachable.
        if (memoryEnabled)
        {
            try
            {
                using IServiceScope initScope = host.Services.CreateScope();
                var initializer = initScope.ServiceProvider
                    .GetRequiredService<IMemorySchemaInitializer>();
                await initializer.InitializeAsync(embeddingDimensions, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine(
                    $"[Agency] Memory is enabled but the store/embeddings are unreachable: {ex.Message}");
                System.Console.Error.WriteLine(
                    "[Agency] Ensure the configured Memory:Provider backend (Postgres requires its docker " +
                    "container) and the embeddings endpoint are reachable, or set Memory:Enabled=false in " +
                    "appsettings.json to run without memory.");
                throw;
            }
        }

        using (var scope = host.Services.CreateScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<ConsoleChatSession>();
            await app.RunAsync();
        }

        // Serilog/Log cleanup
        await Log.CloseAndFlushAsync();
    }
}
