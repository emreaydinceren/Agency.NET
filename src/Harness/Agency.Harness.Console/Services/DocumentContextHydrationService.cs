using Agency.VectorStore.Common;
using System.Globalization;
using System.Text;

namespace Agency.Harness.Console.Services;

internal sealed class DocumentContextHydrationService(
    IVectorStore vectorStore,
    IProjectSessionState sessionState)
{
    private bool _isDirty = true;
    private string? _cachedFact;

    public bool IsDirty => this._isDirty;

    public void MarkDirty() => this._isDirty = true;

    public async Task<string?> RefreshIfDirtyAsync(CancellationToken ct = default)
    {
        if (!this._isDirty)
        {
            return this._cachedFact;
        }

        IReadOnlyList<DocumentInfo> docs = await vectorStore.ListDocumentsAsync(
            sessionState.UserId,
            sessionState.SessionId,
            sessionState.LoadedProjects,
            ct);

        this._isDirty = false;

        if (docs.Count == 0)
        {
            this._cachedFact = null;
            return null;
        }

        this._cachedFact = BuildFact(docs, sessionState.SessionId);
        return this._cachedFact;
    }

    private static string BuildFact(IReadOnlyList<DocumentInfo> docs, string sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The following documents have been ingested and are available for semantic_search:");

        foreach (DocumentInfo doc in docs.OrderBy(d => d.SourceFile))
        {
            string scope = (doc.SessionId, doc.ProjectId) switch
            {
                ("*", "*") => "global",
                (var sid, "*") when sid == sessionId => "session",
                (_, "*") => "session",   // fallback
                ("*", var pid) => $"project:{pid}",
                _ => "unknown"
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"- [{scope}] {doc.SourceFile}");
        }

        return sb.ToString().TrimEnd();
    }
}
