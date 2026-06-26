global using System.Runtime.CompilerServices;

using Agency.Embeddings.Common;
using Agency.Embeddings.OpenAI;
using Agency.Harness.Console.Commands;
using Agency.Harness.Console.Configuration;
using Agency.Harness.Console.Services;
using Agency.Harness.Console.Telemetry;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Permissions;
using Agency.Harness.Skills;
using Agency.Harness.Tools;
using Agency.Ingestion;
using Agency.Ingestion.SemanticKernel;
using Agency.Memory.Consolidator.DependencyInjection;
using Agency.Memory.Distiller;
using Agency.Memory.Distiller.DependencyInjection;
using Agency.Memory.Hygiene.DependencyInjection;
using Agency.Memory.Common.Storage;
using Agency.Memory.Sql.Postgres;
using Agency.Memory.Sql.Sqlite;
using Agency.Llm.OpenAI;
using Agency.Llm.Common;
using Agency.Llm.Common.Tools;
using Agency.Sql.Postgres;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Agency.VectorStore.Sql.Postgres;
using Agency.VectorStore.Sql.Sqlite;
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

        // Deterministic clock for functional cache-replay: under DOTNET_ENVIRONMENT=Test the
        // agent's "Current date/time (UTC)" line is frozen to a fixed literal so console agent
        // turns produce byte-identical request bodies on every run (local record and CI replay).
        // Production registers nothing here, so the agent uses TimeProvider.System as before.
        if (builder.Environment.IsEnvironment("Test"))
        {
            builder.Services.AddSingleton<TimeProvider>(
                new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }

        // 4. Options Pattern:
        // Resolve a stable per-installation user id (generated once, persisted to appsettings.json) before
        // binding so AgentOptions.UserId is populated. The id partitions memory and is substituted for the
        // {userId} placeholder in tool calls by UserIdPlaceholderHook. Skipped under Test to keep functional
        // cache-replay deterministic and avoid writing to the test appsettings.
        if (builder.Environment.IsEnvironment("Test") == false)
        {
            string appSettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
            UserIdConfiguration.EnsureUserId(builder.Configuration, appSettingsPath, static () => Guid.NewGuid().ToString());
        }

        builder.Services.AddOptions<AgentOptions>()
            .BindConfiguration("Agent")
            .ValidateOnStart();

        // Substitute {userId} placeholders in tool arguments with the resolved user id (host-owned identity).
        builder.Services.PostConfigure<AgentOptions>(options =>
            options.UserHooks = options.UserHooks is { } existing
                ? existing.Compose(UserIdPlaceholderHook.Hooks)
                : UserIdPlaceholderHook.Hooks);

        builder.Services.AddAgencyConfiguredHooks(builder.Configuration);
        builder.Services.AddAgencyPermissions(builder.Configuration);

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

        // 5.7 Vector store, ingestion, and retrieval.
        //     Options bindings are unconditional so configuration is always validated.
        //     Vector store and text-splitter require IEmbeddingGenerator, so they are gated
        //     on memoryEnabled to avoid startup failures when embeddings are not configured.
        builder.Services.Configure<VectorStoreOptions>(
            builder.Configuration.GetSection(VectorStoreOptions.SectionName));
        builder.Services.Configure<IngestionOptions>(
            builder.Configuration.GetSection(IngestionOptions.SectionName));
        builder.Services.Configure<RetrievalOptions>(
            builder.Configuration.GetSection(RetrievalOptions.SectionName));

        // Session state: stable per-scope UserId/SessionId, independent of memory.
        builder.Services.AddScoped<IProjectSessionState, ProjectSessionState>();

        if (memoryEnabled)
        {
            builder.Services.AddSingleton<ITextSplitter>(sp =>
            {
                IngestionOptions ingestion = sp.GetRequiredService<IOptions<IngestionOptions>>().Value;
                return new SemanticKernelTextSplitter(ingestion.ChunkSize, ingestion.ChunkOverlap);
            });

            builder.Services.AddSingleton<IVectorStore>(sp =>
            {
                string provider = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value.Provider;
                IEmbeddingGenerator embeddings = sp.GetRequiredService<IEmbeddingGenerator>();

                if (provider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
                {
                    string cs = builder.Configuration.GetConnectionString("VectorStorePostgreSql")
                        ?? throw new InvalidOperationException(
                            "ConnectionStrings:VectorStorePostgreSql is required when VectorStore:Provider is 'postgres'.");
                    PostgreSqlRunner runner = new(cs, sp.GetService<ILogger<PostgreSqlRunner>>());
                    return new PostgresKVStore(embeddings, runner, sp.GetRequiredService<ILogger<PostgresKVStore>>());
                }
                else
                {
                    string cs = builder.Configuration.GetConnectionString("VectorStoreSqlite")
                        ?? throw new InvalidOperationException(
                            "ConnectionStrings:VectorStoreSqlite is required when VectorStore:Provider is 'sqlite'.");
                    SqliteRunner runner = new(cs, SqliteKVStore.RegisterVectorFunctions, sp.GetService<ILogger<SqliteRunner>>());
                    return new SqliteKVStore(embeddings, runner, sp.GetService<ILogger<SqliteKVStore>>());
                }
            });

            builder.Services.AddScoped<IngestionCommandService>();
            builder.Services.AddScoped<DocumentContextHydrationService>();
        }

        // 5.5 MCP servers — opt-in via the "Mcp" config section.
        //     Skipped under Test: MCP servers are external processes that are not present in the
        //     functional-test environment (CI), and their discovered tools would be injected into the
        //     agent's tool list, changing the LLM request body and breaking offline HTTP-cache replay.
        McpClientOptions? mcpOptions = builder.Environment.IsEnvironment("Test")
            ? null
            : builder.Configuration.GetSection("Mcp").Get<McpClientOptions>();
        if (mcpOptions is { Servers.Length: > 0 })
        {
            // Expand ${RepoRoot}/${Configuration} tokens so committed server paths stay portable
            // across machines, drives, OSes and build configurations.
            string repoRoot = McpConfigResolver.FindRepoRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
            string configuration = McpConfigResolver.ResolveConfiguration(AppContext.BaseDirectory);
            McpConfigResolver.Expand(mcpOptions, repoRoot, configuration);

            try
            {
                var pool = await McpClientPool.CreateAsync(mcpOptions);
                builder.Services.AddSingleton(pool);
                System.Console.WriteLine($"[Agency] MCP: connected {mcpOptions.Servers.Length} server(s), {pool.Tools.Count} tool(s).");
            }
            catch (Exception ex)
            {
                System.Console.Error.WriteLine($"[Agency] MCP servers configured but unreachable: {ex.Message}");
                throw;
            }
        }

        // 5.6 Skills — discover skill directories at startup and make the catalog available as a singleton.
        //     Config key: Skills:Directories (string[]). Defaults to ["./.agency/skills", "~/.agency/skills"]
        //     in project-first order so project skills override personal skills (first-occurrence wins).
        //
        //     A ReloadableSkillCatalog is used so that SkillContext and SkillTool pick up SKILL.md
        //     changes live (they read through the shared reference). A SkillWatcher drives reloads
        //     via FileSystemWatcher + debounce whenever SKILL.md files are added, edited, or removed.
        //
        //     KNOWN LIMITATION: /skill-name console commands (registered below) are built once at
        //     startup from the initial catalog snapshot. Newly-added skills will appear in the model's
        //     system-prompt catalog and be invocable via the `skill` tool immediately after reload,
        //     but won't appear as /commands in the console until the next restart.
        string[] configuredSkillDirs = builder.Configuration
            .GetSection("Skills:Directories")
            .Get<string[]>() ?? [];
        IReadOnlyList<string> skillRoots = configuredSkillDirs.Length > 0
            ? configuredSkillDirs
            :
            [
                Path.Combine(Directory.GetCurrentDirectory(), ".agency", "skills"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agency", "skills"),
            ];
        ReloadableSkillCatalog reloadableCatalog = new(skillRoots);
        builder.Services.AddSingleton<ISkillCatalog>(reloadableCatalog);
        builder.Services.AddSingleton(new SkillContext { Catalog = reloadableCatalog });
        // Register the watcher as a singleton so it lives for the application lifetime and is disposed
        // when the host shuts down. The watcher fires reloadableCatalog.Reload() on SKILL.md changes.
        builder.Services.AddSingleton(new SkillWatcher(skillRoots, reloadableCatalog.Reload));
        CommandRegistry.RegisterSkillCommands(reloadableCatalog);
        System.Console.WriteLine($"[Agency] Skills: loaded {reloadableCatalog.List().Count} skill(s) from {skillRoots.Count} root(s).");

        // 6. Factory & Agent Registration:
        //    Models + IAgentFactory + the scoped default Agent now live in the Agency.Harness
        //    library (host-independent agent assembly). The host only owns the wiring call.
        builder.Services.AddAgencyAgent();

        // 7. Tool & Context Registration:
        bool disableSkillShell = builder.Configuration.GetValue<bool>("Skills:DisableShellExecution");

        builder.Services.AddScoped(sp =>
        {
            var agentFactory = sp.GetRequiredService<IAgentFactory>();
            var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;

            var inner = new ToolRegistry();
            IToolRegistry outward = null!;
            inner.Register(new ExecutePowershellTool());
            inner.Register(new ReadFileTool());
            inner.Register(new WriteFileTool());

            inner.Register(new AgentTool((clientName, modelName) =>
                (options, agentFactory.CreateAgent(clientName, modelName), outward)));

            inner.Register(new SkillTool(
                sp.GetRequiredService<ISkillCatalog>(),
                shellRunner: new PowerShellSkillShellRunner(),
                disableShellExecution: disableSkillShell,
                forkRunner: async (prompt, agentType, ct) =>
                {
                    Agent forkAgent = agentFactory.CreateAgent(null, null);
                    await using ChatSession forkSession = new(forkAgent, options, new ToolContext { Registry = outward });
                    string forkResult = string.Empty;
                    await foreach (AgentEvent ev in forkSession.SendAsync(prompt, ct).ConfigureAwait(false))
                    {
                        if (ev is AgentResultEvent resultEv)
                        {
                            forkResult = resultEv.FinalText ?? string.Empty;
                        }
                    }
                    return forkResult;
                }));

            var mcpToolNames = new HashSet<string>();
            var mcpPool = sp.GetService<McpClientPool>();   // null when MCP is not configured
            if (mcpPool is not null)
            {
                foreach (ITool tool in mcpPool.Tools)
                {
                    inner.Register(tool);
                    mcpToolNames.Add(tool.Definition.Name);
                }
            }

            // Semantic search — only registered when the vector store is available.
            IVectorStore? vectorStore = sp.GetService<IVectorStore>();
            if (vectorStore is not null)
            {
                IProjectSessionState sessionState = sp.GetRequiredService<IProjectSessionState>();
                RetrievalOptions retrieval = sp.GetRequiredService<IOptions<RetrievalOptions>>().Value;
                inner.Register(new SemanticSearchTool(vectorStore, sessionState, retrieval.TopK));
            }

            // Progressive discovery applies to MCP tools only: their schemas are withheld behind
            // tool_help. Native/internal tools are revealed in full.
            outward = options.ProgressiveDiscovery
                ? new ProgressiveDiscoveryToolRegistry(inner, mcpToolNames)
                : inner;
            return new ToolContext { Registry = outward };
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

                IVectorStore vectorStore = initScope.ServiceProvider.GetRequiredService<IVectorStore>();
                if (vectorStore is SqliteKVStore sqliteKVStore)
                {
                    await sqliteKVStore.InitializeSchemaAsync(embeddingDimensions, CancellationToken.None);
                }
                else if (vectorStore is PostgresKVStore postgresKVStore)
                {
                    await postgresKVStore.InitializeSchemaAsync(embeddingDimensions, CancellationToken.None);
                }
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

        // Resolve the SkillWatcher singleton to start filesystem monitoring for the app lifetime.
        using SkillWatcher skillWatcher = host.Services.GetRequiredService<SkillWatcher>();

        using (var scope = host.Services.CreateScope())
        {
            var app = scope.ServiceProvider.GetRequiredService<ConsoleChatSession>();
            await app.RunAsync();
        }

        // Serilog/Log cleanup
        await Log.CloseAndFlushAsync();
    }
}
