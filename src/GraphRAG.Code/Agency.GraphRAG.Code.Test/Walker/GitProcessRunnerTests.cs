using System.Diagnostics;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Walker;

/// <summary>
/// Tests for <see cref="GitProcessRunner"/> using a real git repository.
/// </summary>
public sealed class GitProcessRunnerTests : IDisposable
{
    private readonly List<string> _directoriesToDelete = [];

    [Fact]
    public void LsFiles_ReturnsTrackedFiles()
    {
        SkipIfGitMissing();

        string repositoryPath = CreateTempDirectory();
        InitializeRepository(repositoryPath);
        WriteFile(repositoryPath, Path.Combine("src", "Tracked.cs"), "class Tracked {}");
        WriteFile(repositoryPath, Path.Combine("docs", "tracked.ts"), "export const tracked = true;");
        Git(repositoryPath, "add .");
        Commit(repositoryPath, "initial");

        var runner = new GitProcessRunner();

        IReadOnlyList<string> files = runner.LsFiles(repositoryPath);

        Assert.Equal(["docs/tracked.ts", "src/Tracked.cs"], files.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void DiffNameStatus_ParsesAddModifyDeleteAndRename()
    {
        SkipIfGitMissing();

        string repositoryPath = CreateTempDirectory();
        InitializeRepository(repositoryPath);
        WriteFile(repositoryPath, "modified.cs", "class Before {}");
        WriteFile(repositoryPath, "deleted.py", "print('delete')");
        WriteFile(repositoryPath, "old-name.js", "console.log('rename');");
        Git(repositoryPath, "add .");
        Commit(repositoryPath, "initial");
        string fromCommit = Git(repositoryPath, "rev-parse HEAD");

        WriteFile(repositoryPath, "modified.cs", "class After {}");
        DeleteFile(repositoryPath, "deleted.py");
        RenameFile(repositoryPath, "old-name.js", "new-name.js");
        WriteFile(repositoryPath, "added.ts", "export const added = true;");
        Git(repositoryPath, "add -A");
        Commit(repositoryPath, "second");
        string toCommit = Git(repositoryPath, "rev-parse HEAD");

        var runner = new GitProcessRunner();

        IReadOnlyList<GitDiffEntry> entries = runner.DiffNameStatus(repositoryPath, fromCommit, toCommit);

        Assert.Contains(entries, entry => entry is { Status: 'A', OldPath: null, NewPath: "added.ts" });
        Assert.Contains(entries, entry => entry is { Status: 'M', OldPath: null, NewPath: "modified.cs" });
        Assert.Contains(entries, entry => entry is { Status: 'D', OldPath: "deleted.py", NewPath: null });
        Assert.Contains(entries, entry => entry is { Status: 'R', OldPath: "old-name.js", NewPath: "new-name.js" });
    }

    [Fact]
    public void RevParseHead_ReturnsCurrentHead()
    {
        SkipIfGitMissing();

        string repositoryPath = CreateTempDirectory();
        InitializeRepository(repositoryPath);
        WriteFile(repositoryPath, "file.cs", "class Sample {}");
        Git(repositoryPath, "add .");
        Commit(repositoryPath, "initial");
        string expectedHead = Git(repositoryPath, "rev-parse HEAD");

        var runner = new GitProcessRunner();

        string actualHead = runner.RevParseHead(repositoryPath);

        Assert.Equal(expectedHead, actualHead);
    }

    [Fact]
    public void IsShallowRepository_ReturnsExpectedValue()
    {
        SkipIfGitMissing();

        string sourceRepositoryPath = CreateTempDirectory();
        InitializeRepository(sourceRepositoryPath);
        WriteFile(sourceRepositoryPath, "file.cs", "class Sample {}");
        Git(sourceRepositoryPath, "add .");
        Commit(sourceRepositoryPath, "initial");

        string fullClonePath = CreateTempDirectory();
        Git(Directory.GetCurrentDirectory(), $"clone --no-local \"{sourceRepositoryPath}\" \"{fullClonePath}\"");

        string shallowClonePath = CreateTempDirectory();
        Git(Directory.GetCurrentDirectory(), $"clone --no-local --depth 1 \"{sourceRepositoryPath}\" \"{shallowClonePath}\"");

        var runner = new GitProcessRunner();

        Assert.False(runner.IsShallowRepository(fullClonePath));
        Assert.True(runner.IsShallowRepository(shallowClonePath));
    }

    [Fact]
    public void IsAncestor_ReturnsTrueOnlyForAncestorRelationship()
    {
        SkipIfGitMissing();

        string repositoryPath = CreateTempDirectory();
        InitializeRepository(repositoryPath);
        WriteFile(repositoryPath, "file.cs", "class One {}");
        Git(repositoryPath, "add .");
        Commit(repositoryPath, "first");
        string firstCommit = Git(repositoryPath, "rev-parse HEAD");

        WriteFile(repositoryPath, "file.cs", "class Two {}");
        Git(repositoryPath, "add .");
        Commit(repositoryPath, "second");
        string secondCommit = Git(repositoryPath, "rev-parse HEAD");

        var runner = new GitProcessRunner();

        Assert.True(runner.IsAncestor(repositoryPath, firstCommit, secondCommit));
        Assert.False(runner.IsAncestor(repositoryPath, secondCommit, firstCommit));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (string directory in this._directoriesToDelete)
        {
            if (Directory.Exists(directory))
            {
                ResetAttributes(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void SkipIfGitMissing()
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Failed to start git process.");

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Assert.Skip("git is not available on PATH.");
            }
        }
        catch (Exception)
        {
            Assert.Skip("git is not available on PATH.");
        }
    }

    private string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Agency.GraphRAG.Code.Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        this._directoriesToDelete.Add(path);
        return path;
    }

    private static void InitializeRepository(string repositoryPath)
    {
        Git(repositoryPath, "init");
        Git(repositoryPath, "config user.name \"Agency Tests\"");
        Git(repositoryPath, "config user.email \"agency-tests@example.com\"");
    }

    private static void Commit(string repositoryPath, string message)
    {
        Git(repositoryPath, $"commit -m \"{message}\"");
    }

    private static void WriteFile(string repositoryPath, string relativePath, string content)
    {
        string fullPath = Path.Combine(repositoryPath, relativePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    private static void DeleteFile(string repositoryPath, string relativePath)
    {
        string fullPath = Path.Combine(repositoryPath, relativePath);
        File.Delete(fullPath);
    }

    private static void RenameFile(string repositoryPath, string oldRelativePath, string newRelativePath)
    {
        string oldFullPath = Path.Combine(repositoryPath, oldRelativePath);
        string newFullPath = Path.Combine(repositoryPath, newRelativePath);
        File.Move(oldFullPath, newFullPath);
    }

    private static string Git(string workingDirectory, string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start git process.");

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {arguments} failed with exit code {process.ExitCode}: {standardError.Trim()}");
        }

        return standardOutput.Trim();
    }

    private static void ResetAttributes(string path)
    {
        foreach (string directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        File.SetAttributes(path, FileAttributes.Normal);
    }
}
