namespace Agency.Harness.Console.Configuration;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    public int ChunkSize { get; init; } = 512;
    public int ChunkOverlap { get; init; } = 64;
    public string SearchPattern { get; init; } = "*.md";
}
