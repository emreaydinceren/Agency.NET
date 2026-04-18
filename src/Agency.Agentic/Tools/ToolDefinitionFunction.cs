namespace Agency.Agentic.Tools;

/// <summary>
/// Creates <see cref="AIFunctionDeclaration"/> instances from <see cref="ToolDefinition"/>s so
/// they can be passed to <see cref="ChatOptions.Tools"/>. Invocation is always handled manually
/// by the agent loop via <see cref="IToolRegistry"/>; the declarations are non-invocable by design.
/// </summary>
internal static class ToolDefinitionFunction
{
    /// <summary>Wraps a <see cref="ToolDefinition"/> as a non-invocable <see cref="AIFunctionDeclaration"/>.</summary>
    internal static AIFunctionDeclaration Create(ToolDefinition def) =>
        AIFunctionFactory.CreateDeclaration(def.Name, def.Description, def.InputSchema, null);
}
