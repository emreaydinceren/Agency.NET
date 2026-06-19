using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Agency.Harness.Skills;

/// <summary>
/// Executes a shell command string and returns its combined stdout output.
/// Implementations are responsible for selecting the appropriate interpreter.
/// </summary>
internal interface ISkillShellRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> in the shell and returns the captured output.
    /// </summary>
    /// <param name="command">The command text to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stdout string produced by the command.</returns>
    Task<string> RunAsync(string command, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ISkillShellRunner"/> implementation that executes commands via a PowerShell runspace,
/// reusing the same execution path as <see cref="Agency.Harness.Tools.ExecutePowershellTool"/>.
/// </summary>
internal sealed class PowerShellSkillShellRunner : ISkillShellRunner
{
    /// <inheritdoc/>
    public Task<string> RunAsync(string command, CancellationToken ct = default)
    {
        using Runspace runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        runspace.Open();

        using PowerShell ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddScript(command);

        System.Collections.ObjectModel.Collection<PSObject> results = ps.Invoke();

        if (ps.HadErrors)
        {
            string errors = string.Join("\n", ps.Streams.Error.Select(static e => e.ToString()));
            return Task.FromResult(errors);
        }

        if (results.Count == 0)
        {
            return Task.FromResult(string.Empty);
        }

        Runspace? previous = Runspace.DefaultRunspace;
        Runspace.DefaultRunspace = runspace;

        try
        {
            string output = string.Join("\n", results.Select(static r => r?.ToString() ?? string.Empty));
            return Task.FromResult(output);
        }
        finally
        {
            Runspace.DefaultRunspace = previous;
        }
    }
}
