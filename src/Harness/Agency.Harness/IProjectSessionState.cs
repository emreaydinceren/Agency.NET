namespace Agency.Harness;

/// <summary>
/// Tracks the current user/session identity and the set of projects loaded into scope, used to
/// partition and scope stored/retrieved records (e.g. for <see cref="Tools.SemanticSearchTool"/>).
/// </summary>
public interface IProjectSessionState
{
    /// <summary>Gets the identifier of the current user, used to scope stored/retrieved records.</summary>
    string UserId { get; }

    /// <summary>Gets the identifier of the current session.</summary>
    string SessionId { get; }

    /// <summary>Gets the ids of the projects currently loaded into scope.</summary>
    IReadOnlyList<string> LoadedProjects { get; }

    /// <summary>Loads the named project into scope, making its records visible to scoped queries.</summary>
    /// <param name="projectName">The name of the project to load.</param>
    void LoadProject(string projectName);

    /// <summary>Removes the named project from scope.</summary>
    /// <param name="projectName">The name of the project to unload.</param>
    void UnloadProject(string projectName);
}
