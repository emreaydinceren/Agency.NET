namespace Agency.Llm.Common;

/// <summary>Provides the list of models available from a given LLM provider.</summary>
public interface IModelProvider
{
    /// <summary>Returns all models available through this provider.</summary>
    Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default);
}
