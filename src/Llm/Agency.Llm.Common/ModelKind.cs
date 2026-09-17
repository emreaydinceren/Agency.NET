namespace Agency.Llm.Common;

/// <summary>The functional category of an LLM model.</summary>
public enum ModelKind
{
    /// <summary>The provider reported a kind that does not map to a known category.</summary>
    Unknown,

    /// <summary>A chat/completion model.</summary>
    Chat,

    /// <summary>An embedding model.</summary>
    Embedding,

    /// <summary>A vision-capable model.</summary>
    Vision,
}
