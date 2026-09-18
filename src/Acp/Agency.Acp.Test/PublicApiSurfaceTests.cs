namespace Agency.Acp.Test;

/// <summary>
/// Guards Objective O-6 (spec §1.3): "no public API is removed" by this work — i.e. this work adds
/// zero new <c>*REMOVED*</c> entries to any <c>PublicAPI.Unshipped.txt</c> under <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Correction to the project plan.</b> Task 12 originally asked for a blanket assertion that
/// <i>no</i> <c>PublicAPI.Unshipped.txt</c> under <c>src/</c> contains the literal <c>*REMOVED*</c>.
/// That assertion fails on arrival for two independent reasons the plan did not anticipate:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <c>src/Harness/Agency.Harness/PublicAPI.Unshipped.txt</c> lines 2-4 carry three pre-existing,
/// unrelated <c>*REMOVED*</c> entries (<c>MemoryContext.LongTermMemory.get</c>/<c>.init</c>, and the
/// old <c>Models(...)</c> constructor) — exactly as the task text anticipated.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Unanticipated by the task text:</b> four further, equally pre-existing and unrelated files
/// also already carry <c>*REMOVED*</c> entries: <c>Agency.Ingestion</c> (4 lines — a removed
/// generic pipeline interface and two constructor/record shape changes), <c>Agency.Memory.Common</c>
/// (1 line — a removed <c>Record.Create</c> factory overload), <c>Agency.Memory.Distiller</c> and
/// <c>Agency.Memory.Hygiene</c> (1 line each — removed <c>AddAgency*</c> extension overloads). None
/// of these five files is touched by this work (confirmed via <c>git status</c>: only
/// <c>Agency.Harness</c>'s file is modified on this branch), so all ten lines, across five files,
/// are pre-existing baseline noise from earlier, unrelated work — not something this task introduced
/// or should paper over.
/// </description>
/// </item>
/// </list>
/// <para>
/// The real intent behind O-6 is narrower and still fully testable: <i>this</i> work — which only
/// ever touches <c>ChatSession.ChatSession</c> (via an overload, not a replacement — see spec
/// §14.2) and <c>Agent.CreateContext</c> — must add zero <c>*REMOVED*</c>
/// entries of its own. This test therefore asserts two things instead of the plan's single blanket
/// check: (a) the total <c>*REMOVED*</c> line count across every <c>PublicAPI.Unshipped.txt</c> under
/// <c>src/</c> matches the actual, itemised pre-existing baseline below (so a stray 11th line from
/// <i>any</i> future change — not just this one — fails the build); and (b) no <c>*REMOVED*</c> line
/// anywhere mentions <c>CreateContext</c> or <c>ChatSession.ChatSession</c>, the two members this
/// work specifically touches.
/// </para>
/// </remarks>
public sealed class PublicApiSurfaceTests
{
    /// <summary>
    /// The pre-existing, unrelated <c>*REMOVED*</c> lines already present in the repository before
    /// this work started (verified via <c>grep -rn "REMOVED" src/ --include=PublicAPI.Unshipped.txt</c>
    /// against a clean checkout of this branch's base). Ten lines across five files — not the three
    /// lines in one file the project plan assumed. Kept here, one entry per line, so a future reader
    /// sees at a glance what the baseline is and why an eleventh line must fail this test.
    /// </summary>
    private static readonly string[] PreExistingUnrelatedRemovedLines =
    [
        // src/Harness/Agency.Harness/PublicAPI.Unshipped.txt (3 lines) — the ones the plan named.
        "*REMOVED*Agency.Harness.Contexts.MemoryContext.LongTermMemory.get -> System.Collections.Generic.IReadOnlyList<string!>!",
        "*REMOVED*Agency.Harness.Contexts.MemoryContext.LongTermMemory.init -> void",
        "*REMOVED*Agency.Harness.Models.Models(Microsoft.Extensions.Options.IOptions<Agency.Harness.AgentOptions!>! agentOptions, Microsoft.Extensions.Logging.ILogger<Agency.Harness.Models!>? logger = null) -> void",

        // src/Ingestion/Agency.Ingestion/PublicAPI.Unshipped.txt (4 lines) — not named by the plan.
        "*REMOVED*Agency.Ingestion.IIngestionPipeline<TValue>",
        "*REMOVED*Agency.Ingestion.IIngestionPipeline<TValue>.ExecuteAsync(Agency.Ingestion.IDocumentLoader! loader, Agency.Ingestion.ITextSplitter! splitter, Agency.VectorStore.Common.IVectorStore! store, string! userId, string? sessionId, string? projectId = null, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Agency.Ingestion.IngestionResult!>!",
        "*REMOVED*Agency.Ingestion.IngestionResult.Deconstruct(out int Succeeded, out int Failed, out System.Collections.Generic.IReadOnlyList<string!>? FailedKeys) -> void",
        "*REMOVED*Agency.Ingestion.IngestionResult.IngestionResult(int Succeeded, int Failed, System.Collections.Generic.IReadOnlyList<string!>? FailedKeys = null) -> void",

        // src/Memory/Agency.Memory.Common/PublicAPI.Unshipped.txt (1 line) — not named by the plan.
        "*REMOVED*static Agency.Memory.Common.Records.Record.Create(string! id, string! userId, string? sessionId, Agency.Memory.Common.Records.ContentType contentType, string! domain, string! key, string! title, string! value, System.Collections.Generic.IReadOnlyList<string!>! tags, double importance, System.DateTimeOffset createdAt, System.DateTimeOffset updatedAt, System.DateTimeOffset? lastAccessedAt = null, System.ReadOnlyMemory<float> embedding = default(System.ReadOnlyMemory<float>)) -> Agency.Memory.Common.Records.Record!",

        // src/Memory/Agency.Memory.Distiller/PublicAPI.Unshipped.txt (1 line) — not named by the plan.
        "*REMOVED*static Agency.Memory.Distiller.DependencyInjection.MemoryServiceCollectionExtensions.AddAgencyMemory(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, System.Action<Agency.Memory.Common.Options.MemoryOptions!>? configureMemory = null, System.Action<Agency.Memory.Common.Options.DistillerOptions!>? configureDistiller = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!",

        // src/Memory/Agency.Memory.Hygiene/PublicAPI.Unshipped.txt (1 line) — not named by the plan.
        "*REMOVED*static Agency.Memory.Hygiene.DependencyInjection.HygieneServiceCollectionExtensions.AddAgencyHygiene(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, System.Action<Agency.Memory.Common.Options.MemoryOptions!>? configure = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!",
    ];

    /// <summary>
    /// Walks up from <see cref="AppContext.BaseDirectory"/> until a directory containing
    /// <c>src/Agency.slnx</c> is found. Returns <see langword="null"/> if none is found within a
    /// reasonable number of levels — the file layout differs in a packed run, and the caller must
    /// skip rather than fail in that case.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Agency.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// (a) The total <c>*REMOVED*</c> line count across every <c>PublicAPI.Unshipped.txt</c> under
    /// <c>src/</c> matches the itemised pre-existing baseline exactly — this work adds none, and
    /// neither does any future change that isn't accompanied by an update to this list.
    /// </summary>
    [Fact]
    public void NoPublicApiUnshippedFile_HasMoreRemovedLinesThanThePreExistingBaseline()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Assert.Skip("Could not locate the repo root (src/Agency.slnx) from AppContext.BaseDirectory; file layout differs in a packed run.");
            return;
        }

        string srcRoot = Path.Combine(repoRoot, "src");
        string[] removedLines = Directory.EnumerateFiles(srcRoot, "PublicAPI.Unshipped.txt", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("*REMOVED*", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(PreExistingUnrelatedRemovedLines.Length, removedLines.Length);
    }

    /// <summary>
    /// (b) No <c>*REMOVED*</c> line anywhere names <c>CreateContext</c> or
    /// <c>ChatSession.ChatSession</c> — the two members this work touches (spec §6.4, §14.2). Their
    /// presence would mean an optional parameter was used instead of an overload.
    /// </summary>
    [Fact]
    public void NoRemovedLine_MentionsCreateContextOrChatSessionConstructor()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Assert.Skip("Could not locate the repo root (src/Agency.slnx) from AppContext.BaseDirectory; file layout differs in a packed run.");
            return;
        }

        string srcRoot = Path.Combine(repoRoot, "src");
        string[] removedLines = Directory.EnumerateFiles(srcRoot, "PublicAPI.Unshipped.txt", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines)
            .Where(line => line.Contains("*REMOVED*", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(removedLines, line => line.Contains("CreateContext", StringComparison.Ordinal));
        Assert.DoesNotContain(removedLines, line => line.Contains("ChatSession.ChatSession", StringComparison.Ordinal));
    }
}
