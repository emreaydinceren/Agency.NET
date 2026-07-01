namespace Agency.VectorStore.Common;

/// <summary>
/// Describes a document discovered via <see cref="IVectorStore.ListDocumentsAsync"/>, identified by the
/// value of its <c>source_file</c> metadata entry and the scope it is stored under.
/// </summary>
/// <param name="SourceFile">The value of the <c>source_file</c> metadata entry for the entry.</param>
/// <param name="SessionId">The session the entry is scoped to, or <c>"*"</c> for session-global entries.</param>
/// <param name="ProjectId">The project the entry is scoped to, or <c>"*"</c> for the global project.</param>
public sealed record DocumentInfo(string SourceFile, string SessionId, string ProjectId);
