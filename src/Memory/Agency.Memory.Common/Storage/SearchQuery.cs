using Agency.Memory.Common.Records;

namespace Agency.Memory.Common.Storage;

/// <summary>
/// Parameters for a vector similarity search against the memory store.
/// </summary>
/// <param name="UserId">The user whose records are searched.</param>
/// <param name="QueryEmbedding">The query embedding vector.</param>
/// <param name="TopK">The maximum number of results to return.</param>
/// <param name="ContentType">Optional content-type filter; <see langword="null"/> means no filter.</param>
/// <param name="Domain">Optional domain filter; <see langword="null"/> means no filter.</param>
public sealed record SearchQuery(
    string UserId,
    ReadOnlyMemory<float> QueryEmbedding,
    int TopK,
    ContentType? ContentType = null,
    string? Domain = null);
