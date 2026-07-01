using Agency.Harness;
using Agency.Harness.Console.Services;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="ProjectSessionState"/>.
/// </summary>
public sealed class ProjectSessionStateTests
{
    private static ProjectSessionState CreateState(string? userId = "test-user")
    {
        return new ProjectSessionState(Options.Create(new AgentOptions { UserId = userId }));
    }

    /// <summary>
    /// When <see cref="AgentOptions.UserId"/> is configured, it is used as the session's user ID.
    /// </summary>
    [Fact]
    public void Constructor_UsesAgentOptionsUserId_WhenSet()
    {
        ProjectSessionState state = CreateState(userId: "alice");

        Assert.Equal("alice", state.UserId);
    }

    /// <summary>
    /// When <see cref="AgentOptions.UserId"/> is <see langword="null"/>, the session falls back
    /// to the OS environment user name.
    /// </summary>
    [Fact]
    public void Constructor_FallsBackToEnvironmentUserName_WhenUserIdNull()
    {
        ProjectSessionState state = CreateState(userId: null);

        Assert.Equal(Environment.UserName, state.UserId);
    }

    /// <summary>
    /// The session ID generated at construction time does not change across repeated reads.
    /// </summary>
    [Fact]
    public void Constructor_GeneratesStableSessionId()
    {
        ProjectSessionState state = CreateState();

        string first = state.SessionId;
        string second = state.SessionId;

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Each <see cref="ProjectSessionState"/> instance gets its own unique session ID.
    /// </summary>
    [Fact]
    public void Constructor_TwoInstances_HaveDifferentSessionIds()
    {
        ProjectSessionState a = CreateState();
        ProjectSessionState b = CreateState();

        Assert.NotEqual(a.SessionId, b.SessionId);
    }

    /// <summary>
    /// A freshly constructed session has no loaded projects.
    /// </summary>
    [Fact]
    public void LoadedProjects_InitiallyEmpty()
    {
        ProjectSessionState state = CreateState();

        Assert.Empty(state.LoadedProjects);
    }

    /// <summary>
    /// Loading a project adds it to <see cref="ProjectSessionState.LoadedProjects"/>.
    /// </summary>
    [Fact]
    public void LoadProject_AddsToLoadedProjects()
    {
        ProjectSessionState state = CreateState();

        state.LoadProject("my-project");

        Assert.Contains("my-project", state.LoadedProjects);
    }

    /// <summary>
    /// Loading the same project name with different casing does not create a duplicate entry.
    /// </summary>
    [Fact]
    public void LoadProject_IsCaseInsensitiveDuplicate()
    {
        ProjectSessionState state = CreateState();

        state.LoadProject("MyProject");
        state.LoadProject("myproject");

        Assert.Single(state.LoadedProjects);
    }

    /// <summary>
    /// Unloading a loaded project removes it from <see cref="ProjectSessionState.LoadedProjects"/>.
    /// </summary>
    [Fact]
    public void UnloadProject_RemovesFromLoadedProjects()
    {
        ProjectSessionState state = CreateState();
        state.LoadProject("my-project");

        state.UnloadProject("my-project");

        Assert.DoesNotContain("my-project", state.LoadedProjects);
    }

    /// <summary>
    /// Unloading a project that was never loaded is a no-op rather than an error.
    /// </summary>
    [Fact]
    public void UnloadProject_NonExistentProject_DoesNotThrow()
    {
        ProjectSessionState state = CreateState();

        state.UnloadProject("x");
    }
}
