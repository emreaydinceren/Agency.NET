using Agency.Acp.Sessions;
using Agency.Harness;
using Agency.Harness.Tools;

namespace Agency.Acp.Test.Fakes;

/// <summary>
/// Builds a minimal, real <see cref="SessionState"/> for driving <c>Agency.Acp.Turns.TurnDriver</c>
/// in tests: a real <see cref="McpClientPool"/> (empty, no servers — trivial to create and dispose)
/// and a <see cref="FakeServiceScope"/> stand in for the DI-scoped resources SessionState normally
/// owns, matching the pattern already used by <c>SessionRegistryTests</c>.
/// </summary>
internal static class TestSessions
{
    /// <summary>Builds a <see cref="SessionState"/> wrapping <paramref name="chatSession"/>.</summary>
    public static async Task<SessionState> BuildAsync(
        ChatSession chatSession, AgentOptions? options = null, string sessionId = "s1")
    {
        McpClientPool pool = await McpClientPool.CreateAsync(new McpClientOptions());
        return new SessionState
        {
            SessionId = sessionId,
            ChatSession = chatSession,
            Options = options ?? new AgentOptions(),
            McpPool = pool,
            Scope = new FakeServiceScope(),
            Cwd = "/tmp",
        };
    }
}
