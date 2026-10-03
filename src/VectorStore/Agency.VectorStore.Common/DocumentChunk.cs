namespace Agency.VectorStore.Common;

/// <summary>
/// One chunk of a document passed to <see cref="IVectorStore.ReplaceDocumentAsync{TValue}"/>.
/// </summary>
/// <typeparam name="TValue">The type of the stored value.</typeparam>
/// <param name="Key">The key that identifies the chunk within its scope.</param>
/// <param name="Value">The chunk value; it is serialized and embedded the same way as <see cref="IVectorStore.UpsertAsync{TValue}"/>.</param>
/// <param name="Metadata">Optional metadata for the chunk. The store adds the document's <c>source_file</c> entry.</param>
public sealed record DocumentChunk<TValue>(string Key, TValue Value, IDictionary<string, object>? Metadata = null);
