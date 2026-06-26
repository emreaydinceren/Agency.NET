namespace Agency.Harness.Console.Configuration;

public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    public string Provider { get; init; } = "sqlite";
}
