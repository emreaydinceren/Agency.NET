using Agency.Harness;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Console.Services;

internal sealed class ProjectSessionState : IProjectSessionState
{
    private readonly List<string> _loadedProjects = [];

    public ProjectSessionState(IOptions<AgentOptions> options)
    {
        this.UserId = options.Value.UserId ?? System.Environment.UserName;
        this.SessionId = Guid.NewGuid().ToString("N");
    }

    public string UserId { get; }
    public string SessionId { get; }
    public IReadOnlyList<string> LoadedProjects => this._loadedProjects;

    public void LoadProject(string projectName)
    {
        if (!this._loadedProjects.Contains(projectName, StringComparer.OrdinalIgnoreCase))
        {
            this._loadedProjects.Add(projectName);
        }
    }

    public void UnloadProject(string projectName)
    {
        this._loadedProjects.RemoveAll(p => string.Equals(p, projectName, StringComparison.OrdinalIgnoreCase));
    }
}
