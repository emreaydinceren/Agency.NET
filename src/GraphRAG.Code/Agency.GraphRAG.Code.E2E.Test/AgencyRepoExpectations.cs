namespace Agency.GraphRAG.Code.E2E.Test;

/// <summary>
/// Known Agency-repository symbols and terms that E2E assertions depend on.
/// </summary>
public static class AgencyRepoExpectations
{
    /// <summary>Gets the chat-agent subsystem symbols that should always resolve from the Agency repo.</summary>
    public static IReadOnlyList<string> ChatAgentSymbols { get; } =
    [
        "Agent",
        "ChatSession",
        "IConversationManager",
        "InMemoryConversationManager",
        "SystemPromptBuilder",
    ];

    /// <summary>Gets representative LLM client symbols that should resolve from the Agency repo.</summary>
    public static IReadOnlyList<string> LlmClientSymbols { get; } =
    [
        "ClaudeClient",
        "OpenAIClient",
    ];

    /// <summary>Gets the known consumer symbol used in impact-query assertions.</summary>
    public const string ConversationManagerConsumer = "Context";

    /// <summary>Gets the known external package used in dependency-query assertions.</summary>
    public const string ClaudeDependencyPackage = "Anthropic";
}
