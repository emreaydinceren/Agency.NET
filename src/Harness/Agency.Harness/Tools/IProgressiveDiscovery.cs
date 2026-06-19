namespace Agency.Harness.Tools;

/// <summary>
/// Marker interface implemented by <see cref="IToolRegistry"/> decorators that use progressive
/// tool disclosure — advertising stripped schemas to the LLM and revealing full schemas on demand
/// via <c>tool_help</c>. Callers such as <c>SystemPromptBuilder</c> can test for this interface
/// without taking a concrete-type dependency.
/// </summary>
public interface IProgressiveDiscovery { }
