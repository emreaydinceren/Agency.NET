using System.Diagnostics;

namespace Agency.GraphRAG.Code.Walker;

/// <summary>
/// Runs git commands against a repository by shelling out to the git executable.
/// </summary>
public sealed class GitProcessRunner
{
    /// <summary>
    /// Lists tracked files in the repository.
    /// </summary>
    /// <param name="repositoryPath">The repository working tree path.</param>
    /// <returns>The tracked file paths reported by git.</returns>
    public IReadOnlyList<string> LsFiles(string repositoryPath)
    {
        string output = RunGit(repositoryPath, "ls-files");
        return SplitLines(output);
    }

    /// <summary>
    /// Gets name-status diff entries between two commits.
    /// </summary>
    /// <param name="repositoryPath">The repository working tree path.</param>
    /// <param name="fromCommit">The base commit.</param>
    /// <param name="toCommit">The target commit.</param>
    /// <returns>The parsed diff entries.</returns>
    public IReadOnlyList<GitDiffEntry> DiffNameStatus(string repositoryPath, string fromCommit, string toCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(toCommit);

        string output = RunGit(repositoryPath, $"diff {Quote(fromCommit)} {Quote(toCommit)} --name-status -M");
        var entries = new List<GitDiffEntry>();

        foreach (string line in SplitLines(output))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            char status = parts[0][0];
            switch (status)
            {
                case 'A':
                    entries.Add(new GitDiffEntry('A', null, parts[1]));
                    break;
                case 'M':
                    entries.Add(new GitDiffEntry('M', null, parts[1]));
                    break;
                case 'D':
                    entries.Add(new GitDiffEntry('D', parts[1], null));
                    break;
                case 'R':
                    if (parts.Length >= 3)
                    {
                        entries.Add(new GitDiffEntry('R', parts[1], parts[2]));
                    }

                    break;
            }
        }

        return entries;
    }

    /// <summary>
    /// Resolves the HEAD commit SHA.
    /// </summary>
    /// <param name="repositoryPath">The repository working tree path.</param>
    /// <returns>The current HEAD commit SHA.</returns>
    public string RevParseHead(string repositoryPath)
    {
        return RunGit(repositoryPath, "rev-parse HEAD").Trim();
    }

    /// <summary>
    /// Determines whether the repository is shallow.
    /// </summary>
    /// <param name="repositoryPath">The repository working tree path.</param>
    /// <returns><see langword="true"/> when the repository is shallow; otherwise <see langword="false"/>.</returns>
    public bool IsShallowRepository(string repositoryPath)
    {
        return string.Equals(
            RunGit(repositoryPath, "rev-parse --is-shallow-repository").Trim(),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines whether one commit is an ancestor of another.
    /// </summary>
    /// <param name="repositoryPath">The repository working tree path.</param>
    /// <param name="ancestorCommit">The candidate ancestor commit.</param>
    /// <param name="descendantCommit">The candidate descendant commit.</param>
    /// <returns><see langword="true"/> when <paramref name="ancestorCommit"/> is an ancestor of <paramref name="descendantCommit"/>.</returns>
    public bool IsAncestor(string repositoryPath, string ancestorCommit, string descendantCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ancestorCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(descendantCommit);

        GitCommandResult result = RunGitWithExitCode(
            repositoryPath,
            $"merge-base --is-ancestor {Quote(ancestorCommit)} {Quote(descendantCommit)}",
            allowExitCodes: [0, 1]);

        return result.ExitCode == 0;
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static IReadOnlyList<string> SplitLines(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string RunGit(string repositoryPath, string arguments)
    {
        GitCommandResult result = RunGitWithExitCode(repositoryPath, arguments, allowExitCodes: [0]);
        return result.StandardOutput;
    }

    private static GitCommandResult RunGitWithExitCode(string repositoryPath, string arguments, IReadOnlyCollection<int> allowExitCodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process.");

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!allowExitCodes.Contains(process.ExitCode))
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with exit code {process.ExitCode}: {standardError.Trim()}");
        }

        return new GitCommandResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError);
}

/// <summary>
/// Represents a parsed git diff name-status entry.
/// </summary>
/// <param name="Status">The git status code.</param>
/// <param name="OldPath">The old path for deleted or renamed files.</param>
/// <param name="NewPath">The new path for added, modified, or renamed files.</param>
public sealed record GitDiffEntry(char Status, string? OldPath, string? NewPath);
