namespace Agency.Harness;

public interface IProjectSessionState
{
    string UserId { get; }
    string SessionId { get; }
    IReadOnlyList<string> LoadedProjects { get; }
    void LoadProject(string projectName);
    void UnloadProject(string projectName);
}
