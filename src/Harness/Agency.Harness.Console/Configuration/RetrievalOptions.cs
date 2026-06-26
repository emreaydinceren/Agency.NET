namespace Agency.Harness.Console.Configuration;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    public int TopK { get; init; } = 5;
}
