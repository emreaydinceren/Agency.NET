using Agency.Harness;
using Agency.Llm.Common;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// A test double for <see cref="IAgentFactory"/> that delegates to a caller-supplied delegate,
/// so tests can control exactly which <see cref="Agent"/> (and therefore which
/// <see cref="FakeChatClient"/> and hooks) comes back for a given client/model pair, without
/// touching real LLM configuration or the network.
/// </summary>
internal sealed class FakeAgentFactory : IAgentFactory
{
    private readonly Func<string?, string?, Func<LlmClientOptions, LlmClientOptions>?, Agent> _createAgent;

    /// <param name="createAgent">Called for every <see cref="CreateAgent(string?, string?)"/> with a null transform.</param>
    public FakeAgentFactory(Func<string?, string?, Agent> createAgent)
        : this((clientName, modelName, _) => createAgent(clientName, modelName))
    {
    }

    /// <param name="createAgent">
    /// Called for every <see cref="CreateAgent(string?, string?, Func{LlmClientOptions, LlmClientOptions}?)"/>
    /// call, receiving the transform as-is so tests can apply it to a baseline
    /// <see cref="LlmClientOptions"/> and observe the effective effort settings (spec §6.7).
    /// </param>
    public FakeAgentFactory(Func<string?, string?, Func<LlmClientOptions, LlmClientOptions>?, Agent> createAgent)
    {
        this._createAgent = createAgent ?? throw new ArgumentNullException(nameof(createAgent));
    }

    /// <inheritdoc/>
    public Agent CreateAgent(string? clientName, string? modelName) =>
        this._createAgent(clientName, modelName, null);

    /// <inheritdoc/>
    public Agent CreateAgent(string? clientName, string? modelName, Func<LlmClientOptions, LlmClientOptions>? configureClientOptions) =>
        this._createAgent(clientName, modelName, configureClientOptions);
}
