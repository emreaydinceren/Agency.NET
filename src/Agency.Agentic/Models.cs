namespace Agency.Agentic;

using Agency.Llm.Claude;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Options;

public sealed class Models
{
    private readonly IOptions<AgentOptions> _agentOptions;

    private IEnumerable<LlmClientOptions> _llmClientOptions => this._agentOptions.Value.LLmClients;

    public Models(IOptions<AgentOptions> agentOptions)
    {
        this._agentOptions = agentOptions ?? throw new ArgumentNullException(nameof(agentOptions));
    }

    public async Task<IEnumerable<IGrouping<LlmClientOptions, Model>>> GetAllAsync()
    {
        List<(LlmClientOptions, Model)> pairs = [];

        foreach (var option in this._llmClientOptions)
        {
            ILlmClient client = CreateLlmClient(option);

            foreach (var model in await client.GetModelsAsync())
            {
                pairs.Add((option, model));
            }
        }

        return pairs.GroupBy(s => s.Item1, s => s.Item2);
    }

    public ILlmClient CreateLlmClient(string clientName)
    {
        foreach (var options in this._llmClientOptions)
        {
            if (options.Name.Equals(clientName, StringComparison.OrdinalIgnoreCase))
            {
                return CreateLlmClient(options);
            }
        }

        throw new InvalidOperationException($"No LLM client configuration found with name '{clientName}'.");
    }

    public ILlmClient CreateLlmClient()
        => this.CreateLlmClient(this._agentOptions.Value.DefaultClientName);

    private static ILlmClient CreateLlmClient(LlmClientOptions options)
    {
        return options.ClientType.ToUpperInvariant() switch
        {
            "CLAUDE" => new ClaudeClient(options),
            "OPENAI" => new OpenAIClient(options),
            _ => throw new InvalidOperationException($"Unsupported provider '{options.ClientType}'."),
        };
    }
}
