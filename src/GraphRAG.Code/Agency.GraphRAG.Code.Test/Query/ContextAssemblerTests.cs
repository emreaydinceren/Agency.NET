using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Query;
using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Test.Query;

/// <summary>
/// Tests for <see cref="ContextAssembler"/>.
/// </summary>
public sealed class ContextAssemblerTests
{
    [Fact]
    public void Assemble_Deduplicates_OrdersByStrata_AndTruncatesToBudget()
    {
        ContextAssembler assembler = new();
        Symbol sameFileFirst = CreateSymbol("OrderService", 10);
        Symbol sameFileSecond = CreateSymbol("OrderRepository", 30);
        Symbol extraSymbol = CreateSymbol("OrderNotifier", 50);
        QueryPlan plan = new()
        {
            QueryText = "How does ordering work?",
            Category = QueryCategory.Subsystem,
        };
        QueryRetrievalResult retrieval = new()
        {
            Clusters =
            [
                new QueryClusterResult
                {
                    Cluster = new ClusterRecord
                    {
                        Id = Guid.NewGuid(),
                        Label = "Ordering",
                        Type = ClusterType.Business,
                        CoherenceScore = 0.9,
                        Summary = "Order flow.",
                        Embedding = [1],
                    },
                    Score = 0.9,
                },
                new QueryClusterResult
                {
                    Cluster = new ClusterRecord
                    {
                        Id = retrievalId,
                        Label = "Ordering",
                        Type = ClusterType.Business,
                        CoherenceScore = 0.8,
                        Summary = "Duplicate.",
                        Embedding = [2],
                    },
                    Score = 0.1,
                },
            ],
            Symbols =
            [
                new QuerySymbolResult { Symbol = sameFileSecond, Score = 0.8, Depth = 1, RawCode = "Repo.Save();" },
                new QuerySymbolResult { Symbol = sameFileFirst, Score = 0.9, Depth = 0, RawCode = "Submit();" },
                new QuerySymbolResult { Symbol = sameFileFirst, Score = 0.4, Depth = 0, RawCode = "duplicate" },
                new QuerySymbolResult { Symbol = extraSymbol, Score = 0.3, Depth = 2, RawCode = "notify order created webhook retry queue publish telemetry audit event message dispatch callback" },
            ],
            InfrastructureClusters =
            [
                new QueryClusterResult
                {
                    Cluster = new ClusterRecord
                    {
                        Id = Guid.NewGuid(),
                        Label = "Logging",
                        Type = ClusterType.Infrastructure,
                        CoherenceScore = 0.7,
                        Summary = "Logs.",
                        Embedding = [3],
                    },
                    Score = 0.2,
                },
            ],
            HasLowConfidenceReferences = true,
        };

        QueryContextAssembly assembly = assembler.Assemble(plan, retrieval, tokenBudget: 70);

        string text = assembly.ContextText.ReplaceLineEndings("\n");
        Assert.Contains("Cluster summaries:", text, StringComparison.Ordinal);
        Assert.Contains("Relevant symbols:", text, StringComparison.Ordinal);
        Assert.Contains("Raw code:", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Cluster summaries:", StringComparison.Ordinal) < text.IndexOf("Relevant symbols:", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Relevant symbols:", StringComparison.Ordinal) < text.IndexOf("Raw code:", StringComparison.Ordinal));
        Assert.Contains("Additional infrastructure clusters: Logging", text, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate", text, StringComparison.Ordinal);
        Assert.True(assembly.IsTruncated);
        Assert.True(assembly.EstimatedTokens <= 70);
    }

    private static readonly Guid retrievalId = Guid.NewGuid();

    private static Symbol CreateSymbol(string name, int line) =>
        new()
        {
            Id = Guid.NewGuid(),
            FileId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ModuleId = null,
            Name = name,
            FullyQualifiedName = $"Example.{name}",
            Kind = SymbolKind.Class,
            Signature = $"class {name}",
            Summary = $"{name} summary",
            OneLineSummary = $"{name} one line",
            ContentHash = null,
            Embedding = [1],
            IsUtility = false,
            SourceRangeStart = line,
            SourceRangeEnd = line + 5,
        };
}
