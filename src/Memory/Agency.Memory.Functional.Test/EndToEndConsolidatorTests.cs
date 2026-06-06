using Agency.Memory.Common.Events;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Consolidator.Services;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using System.Text.Json;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end functional test: the Consolidator resolves a contradiction between two records
/// with the same <c>(Domain, Key)</c> and retains the latest value (Spec §6.3, Use Case U3).
/// </summary>
/// <remarks>
/// Uses a stub <see cref="IChatClient"/> that emits a <c>Memory_Update</c> tool call followed by
/// <c>Memory_Done</c>. Does not require a real LM Studio endpoint.
///
/// Requires a running PostgreSQL instance (see README.md).
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>.
/// </remarks>
[Trait("Category", "Functional")]
[Collection("memory-db")]
public sealed class EndToEndConsolidatorTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    private NpgsqlDataSource _dataSource = default!;
    private const int Dim = 4;

    /// <summary>Initialises Postgres and resets schema.</summary>
    public async ValueTask InitializeAsync()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            return;
        }

        this._dataSource = TestInfrastructure.BuildDataSource(_config);
        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, Dim, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── G.5 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the Consolidator sub-agent correctly handles a contradiction:
    /// two records with the same <c>(Domain, Key)</c> but different values.
    /// The stub LLM emits <c>Memory_Update</c> with the newer value, then <c>Memory_Done</c>.
    /// After consolidation, only one record with the latest value must remain,
    /// and its <c>Importance</c> must be &gt;= the original.
    /// </summary>
    [Fact]
    public async Task EndToEnd_ConsolidatorMergesContradiction_LatestStateRetained()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        const string UserId = "user-consolidate";

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        var memOpts = Options.Create(new MemoryOptions());
        var store = new PostgresMemoryStore(
            this._dataSource, embedder, memOpts,
            NullLogger<PostgresMemoryStore>.Instance);

        // ── 1. Seed two contradictory records ────────────────────────────────

        // First: older record — user prefers Postgres.
        MemoryRecord oldRecord = await store.UpsertAsync(
            MemoryRecord.Create(
                id: Guid.NewGuid().ToString(),
                userId: UserId,
                sessionId: "session-old",
                contentType: ContentType.Fact,
                domain: "Preferences",
                key: "Database",
                title: "Database preference",
                value: "User prefers Postgres for data persistence.",
                tags: ["database", "postgres"],
                importance: 0.6,
                createdAt: DateTimeOffset.UtcNow.AddDays(-7),
                updatedAt: DateTimeOffset.UtcNow.AddDays(-7)),
            ct);

        // Second: newer record — user switched to SQLite (same domain+key → different session).
        // Note: to seed two records with the same (domain, key), they need different session ids.
        // The upsert key is (user_id, session_id, domain, key).
        MemoryRecord newRecord = await store.UpsertAsync(
            MemoryRecord.Create(
                id: Guid.NewGuid().ToString(),
                userId: UserId,
                sessionId: "session-new",
                contentType: ContentType.Fact,
                domain: "Preferences",
                key: "Database",
                title: "Database preference (updated)",
                value: "User switched to SQLite for local data storage.",
                tags: ["database", "sqlite"],
                importance: 0.65,
                createdAt: DateTimeOffset.UtcNow.AddDays(-1),
                updatedAt: DateTimeOffset.UtcNow.AddDays(-1)),
            ct);

        double originalImportance = Math.Max(oldRecord.Importance, newRecord.Importance);

        // ── 2. Build stub IChatClient that emits the consolidation tool sequence ──
        //
        // Sequence:
        //   Turn 1: Memory_Update(newRecord.Id, "User switched to SQLite...", importance=0.7)
        //   Turn 2: Memory_Delete(oldRecord.Id)
        //   Turn 3: Memory_Done()

        int stubCallIndex = 0;
        var stubLlm = new Mock<IChatClient>();
        stubLlm
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<ChatMessage> _, ChatOptions? _, CancellationToken _) =>
            {
                stubCallIndex++;
                return stubCallIndex switch
                {
                    1 => BuildToolCallResponse("Memory_Update", $$$"""
                        {
                            "recordId": "{{{newRecord.Id}}}",
                            "newValue": "User switched to SQLite for local data storage.",
                            "newImportance": 0.70
                        }
                        """),
                    2 => BuildToolCallResponse("Memory_Delete", $$$"""
                        {
                            "recordId": "{{{oldRecord.Id}}}"
                        }
                        """),
                    _ => BuildTextResponse("Memory_Done"),
                };
            });

        // ── 3. Build consolidator runner and run it ───────────────────────────

        var consolidatorOpts = Options.Create(new ConsolidatorOptions
        {
            MaxIterations = 10,
            MaxCostUsd = 1.0m,
            Model = "stub-model",
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var mutations = new List<MemoryMutatedEvent>();
        using IDisposable mutationSub = eventBus.Subscribe<MemoryMutatedEvent>((evt, _) =>
        {
            mutations.Add(evt);
            return Task.CompletedTask;
        });

        var runner = ConsolidatorSubAgentFactory.CreateRunner(
            stubLlm.Object,
            "stub-model",
            store,
            consolidatorOpts,
            eventBus,
            NullLogger<Agency.Harness.Agent>.Instance);

        IReadOnlyList<MemoryRecord> allRecords = await store.GetAllForUserAsync(UserId, ct);
        await runner(UserId, allRecords, ct);

        // ── 4. Assert only one record remains ────────────────────────────────

        IReadOnlyList<MemoryRecord> afterRecords = await store.GetAllForUserAsync(UserId, ct);

        // After Memory_Delete(oldRecord) the old record is gone.
        // After Memory_Update(newRecord) the new record's value/importance is updated.
        // After Memory_Done the loop ends.
        // The store should have exactly 1 record for this user.
        Assert.Single(afterRecords);

        MemoryRecord remaining = afterRecords[0];

        // ── 5. Assert value references SQLite (latest fact) ──────────────────
        Assert.Contains("SQLite", remaining.Value, StringComparison.OrdinalIgnoreCase);

        // ── 6. Assert importance >= original ─────────────────────────────────
        Assert.True(
            remaining.Importance >= originalImportance,
            $"Importance {remaining.Importance} should be >= original {originalImportance}.");

        // ── 7. Assert the mutation was surfaced as a MemoryMutatedEvent (TI-8.3) ──
        Assert.NotEmpty(mutations);
        Assert.All(mutations, m => Assert.Equal(UserId, m.UserId));
        Assert.Contains(mutations, m => m.Operation is "Update" or "Delete" or "Merge");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="ChatResponse"/> containing a <see cref="FunctionCallContent"/>
    /// for the given tool name and JSON arguments.
    /// </summary>
    private static ChatResponse BuildToolCallResponse(string toolName, string jsonArguments)
    {
        var callId = Guid.NewGuid().ToString("N");
        var args = JsonDocument.Parse(jsonArguments).RootElement;

        // Convert JsonElement to IDictionary<string, object?> as expected by FunctionCallContent.
        var argDict = new Dictionary<string, object?>();
        foreach (JsonProperty prop in args.EnumerateObject())
        {
            argDict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetDouble(out double d) ? d : (object?)prop.Value.ToString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString(),
            };
        }

        var message = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent(callId, toolName, argDict),
        ]);

        return new ChatResponse([message]);
    }

    /// <summary>
    /// Builds a plain text <see cref="ChatResponse"/> (used for <c>Memory_Done</c>
    /// which the consolidator sub-agent calls as a tool but we simulate as a stop
    /// by not returning a function call — the stop condition fires on the third call).
    /// </summary>
    private static ChatResponse BuildTextResponse(string text)
    {
        var message = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse([message]);
    }
}
