using Agency.Harness;
using Agency.Harness.Console.Services;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Console.Test;

public sealed class ProjectSessionStateTests
{
    private static ProjectSessionState CreateState(string? userId = "test-user")
    {
        return new ProjectSessionState(Options.Create(new AgentOptions { UserId = userId }));
    }

    [Fact]
    public void Constructor_UsesAgentOptionsUserId_WhenSet()
    {
        ProjectSessionState state = CreateState(userId: "alice");

        Assert.Equal("alice", state.UserId);
    }

    [Fact]
    public void Constructor_FallsBackToEnvironmentUserName_WhenUserIdNull()
    {
        ProjectSessionState state = CreateState(userId: null);

        Assert.Equal(Environment.UserName, state.UserId);
    }

    [Fact]
    public void Constructor_GeneratesStableSessionId()
    {
        ProjectSessionState state = CreateState();

        string first = state.SessionId;
        string second = state.SessionId;

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Constructor_TwoInstances_HaveDifferentSessionIds()
    {
        ProjectSessionState a = CreateState();
        ProjectSessionState b = CreateState();

        Assert.NotEqual(a.SessionId, b.SessionId);
    }

    [Fact]
    public void LoadedProjects_InitiallyEmpty()
    {
        ProjectSessionState state = CreateState();

        Assert.Empty(state.LoadedProjects);
    }

    [Fact]
    public void LoadProject_AddsToLoadedProjects()
    {
        ProjectSessionState state = CreateState();

        state.LoadProject("my-project");

        Assert.Contains("my-project", state.LoadedProjects);
    }

    [Fact]
    public void LoadProject_IsCaseInsensitiveDuplicate()
    {
        ProjectSessionState state = CreateState();

        state.LoadProject("MyProject");
        state.LoadProject("myproject");

        Assert.Single(state.LoadedProjects);
    }

    [Fact]
    public void UnloadProject_RemovesFromLoadedProjects()
    {
        ProjectSessionState state = CreateState();
        state.LoadProject("my-project");

        state.UnloadProject("my-project");

        Assert.DoesNotContain("my-project", state.LoadedProjects);
    }

    [Fact]
    public void UnloadProject_NonExistentProject_DoesNotThrow()
    {
        ProjectSessionState state = CreateState();

        state.UnloadProject("x");
    }
}
