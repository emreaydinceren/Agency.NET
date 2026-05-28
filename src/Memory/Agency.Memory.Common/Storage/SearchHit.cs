using Agency.Memory.Common.Records;

namespace Agency.Memory.Common.Storage;

/// <summary>
/// A single result from a <see cref="IMemoryStore.SearchAsync"/> call,
/// pairing a <see cref="Records.Record"/> with its cosine similarity to the query.
/// </summary>
/// <param name="Record">The matched memory record.</param>
/// <param name="Similarity">The cosine similarity in [0, 1] between the query and this record's embedding.</param>
public sealed record SearchHit(Record Record, double Similarity);
