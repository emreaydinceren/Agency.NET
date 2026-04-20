namespace Agency.Ingestion;

/// <summary>
/// Represents a unit of content loaded from a source, carrying both text and provenance metadata.
/// </summary>
public record Document(
    string Content,
    string SourceId,
    Dictionary<string, object>? Metadata = null);
