using Agency.GraphRAG.Code.Domain;
using Npgsql;

namespace Agency.GraphRAG.Code.E2E.Test;

/// <summary>
/// Postgres parity tests for the same Agency-repo query set used by the SQLite E2E pipeline.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresParityTests : IAsyncLifetime
{
    private SqliteHarness? _sqliteHarness;
    private PostgresHarness? _postgresHarness;
    private Query.QueryPipeline? _sqlitePipeline;
    private Query.QueryPipeline? _postgresPipeline;

    public async ValueTask InitializeAsync()
    {
        this._sqliteHarness = E2ETestInfrastructure.CreateSqliteHarness();
        this._postgresHarness = E2ETestInfrastructure.CreatePostgresHarness();

        try
        {
            await this._postgresHarness.InitializeAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            await this._sqliteHarness.DisposeAsync();
            await this._postgresHarness.DisposeAsync();
            this._sqliteHarness = null;
            this._postgresHarness = null;
            return;
        }

        Repo sqliteRepo = new()
        {
            Id = Guid.Parse("d8d2f206-c7de-49c4-bdb5-95fcd729f886"),
            LocalPath = E2ETestInfrastructure.RepoRoot,
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
            RemoteUrl = null,
        };
        Repo postgresRepo = new()
        {
            Id = Guid.Parse("8ca8bf40-d0a7-4f8d-a567-b1c2ad86f799"),
            LocalPath = E2ETestInfrastructure.RepoRoot,
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
            RemoteUrl = null,
        };

        AgencyRepoIndexer sqliteIndexer = new(this._sqliteHarness.Store, this._sqliteHarness.EmbeddingGenerator);
        AgencyRepoIndexer postgresIndexer = new(this._postgresHarness.Store, this._postgresHarness.EmbeddingGenerator);
        IndexArtifacts sqliteArtifacts;
        IndexArtifacts postgresArtifacts;
        try
        {
            sqliteArtifacts = await sqliteIndexer.IndexAsync(sqliteRepo, TestContext.Current.CancellationToken);
            postgresArtifacts = await postgresIndexer.IndexAsync(postgresRepo, TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            await this._sqliteHarness.DisposeAsync();
            await this._postgresHarness.DisposeAsync();
            this._sqliteHarness = null;
            this._postgresHarness = null;
            return;
        }
        sqliteRepo = sqliteRepo with { IndexedCommit = sqliteArtifacts.IndexedCommit };
        postgresRepo = postgresRepo with { IndexedCommit = postgresArtifacts.IndexedCommit };

        this._sqlitePipeline = E2ETestInfrastructure.CreateQueryPipeline(
            this._sqliteHarness.Store,
            new MockChatClient(),
            new SymbolTextProvider(sqliteArtifacts.Files.SelectMany(static file => file.Symbols).GroupBy(static symbol => symbol.Symbol.Id).ToDictionary(static group => group.Key, static group => group.First().Chunk.Content)),
            new InMemoryClusterSource(sqliteArtifacts.Clusters));
        this._postgresPipeline = E2ETestInfrastructure.CreateQueryPipeline(
            this._postgresHarness.Store,
            new MockChatClient(),
            new SymbolTextProvider(postgresArtifacts.Files.SelectMany(static file => file.Symbols).GroupBy(static symbol => symbol.Symbol.Id).ToDictionary(static group => group.Key, static group => group.First().Chunk.Content)),
            new InMemoryClusterSource(postgresArtifacts.Clusters));
    }

    public async ValueTask DisposeAsync()
    {
        if (this._sqliteHarness is not null)
        {
            await this._sqliteHarness.DisposeAsync();
        }

        if (this._postgresHarness is not null)
        {
            await this._postgresHarness.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostgresAnswers_StayWithinOneMentionedSymbolOfSqliteAnswers()
    {
        if (this._postgresHarness is null || this._sqlitePipeline is null || this._postgresPipeline is null)
        {
            Assert.Skip("Postgres+pgvector is not available. Set ConnectionStrings__PostgreSql or start docker-compose to run parity tests.");
        }

        foreach (string question in new[]
                 {
                     "How does chat with agent work?",
                     "What does Agency.Llm.Claude depend on?",
                     "What calls IConversationManager?",
                     "Give me a tour of the codebase.",
                     "What does ChatSession.SendAsync do?",
                 })
        {
            string sqliteAnswer = (await this._sqlitePipeline!.ExecuteAsync(question, TestContext.Current.CancellationToken)).Answer;
            string postgresAnswer = (await this._postgresPipeline!.ExecuteAsync(question, TestContext.Current.CancellationToken)).Answer;

            HashSet<string> sqliteMentions = GetMentionedTerms(sqliteAnswer);
            HashSet<string> postgresMentions = GetMentionedTerms(postgresAnswer);
            HashSet<string> difference = [.. sqliteMentions.Except(postgresMentions, StringComparer.OrdinalIgnoreCase), .. postgresMentions.Except(sqliteMentions, StringComparer.OrdinalIgnoreCase)];
            Assert.True(difference.Count <= 1, $"Question '{question}' diverged too much. SQLite=[{string.Join(", ", sqliteMentions)}], Postgres=[{string.Join(", ", postgresMentions)}]");
        }
    }

    private static HashSet<string> GetMentionedTerms(string answer)
    {
        string[] candidates =
        [
            .. AgencyRepoExpectations.ChatAgentSymbols,
            .. AgencyRepoExpectations.LlmClientSymbols,
            AgencyRepoExpectations.ClaudeDependencyPackage,
            AgencyRepoExpectations.ConversationManagerConsumer,
            "Agent.ChatAsync",
            "ChatSession.SendAsync",
        ];

        return candidates
            .Where(term => answer.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
