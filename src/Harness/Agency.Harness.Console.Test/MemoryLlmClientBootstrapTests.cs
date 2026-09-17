using Agency.Llm.Common;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Verifies that <see cref="Program.BuildMemoryLlmClients"/> — the consolidator/distiller client
/// bootstrap for the memory pipeline — dispatches on <see cref="LlmClientOptions.ClientType"/>
/// instead of hardcoding a provider. Before this fix, both clients were built with a bare
/// <c>new OpenAIClient(...)</c>, so a <c>ClientType: "Claude"</c> default agent client silently got
/// an OpenAI-compatible client for memory work while chatting through Claude everywhere else.
/// </summary>
public sealed class MemoryLlmClientBootstrapTests
{
    private static LlmClientOptions ClaudeOptions() => new()
    {
        Name = "default",
        ClientType = "Claude",
        BaseUrl = "http://localhost:1234",
        ApiKey = "test-key",
    };

    /// <summary>The consolidator client is built via the Claude dispatch branch, not OpenAI's.</summary>
    [Fact]
    public void BuildMemoryLlmClients_ClaudeClientType_ConsolidatorIsNotOpenAI()
    {
        var (consolidator, _) = Program.BuildMemoryLlmClients(ClaudeOptions());

        Assert.Equal("Claude", consolidator.ClientType);
    }

    /// <summary>
    /// The distiller client — built from the same options with <c>SuppressThinking</c> forced on —
    /// is also built via the Claude dispatch branch, not OpenAI's.
    /// </summary>
    [Fact]
    public void BuildMemoryLlmClients_ClaudeClientType_DistillerIsNotOpenAI()
    {
        var (_, distiller) = Program.BuildMemoryLlmClients(ClaudeOptions());

        Assert.Equal("Claude", distiller.ClientType);
    }
}
