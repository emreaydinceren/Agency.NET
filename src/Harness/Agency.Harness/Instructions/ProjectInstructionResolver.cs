using System.Globalization;

namespace Agency.Harness.Instructions;

/// <summary>
/// Resolves instruction files from the filesystem by walking up the directory tree
/// from the working directory to the repository root.
/// </summary>
internal sealed class ProjectInstructionResolver : IInstructionResolver
{
    private readonly IReadOnlyList<string> _fallbackFilenames;

    /// <param name="fallbackFilenames">Filenames to search for, in priority order. Defaults to ["AGENTS.md"].</param>
    public ProjectInstructionResolver(IReadOnlyList<string>? fallbackFilenames = null)
    {
        this._fallbackFilenames = fallbackFilenames ?? new[] { "AGENTS.md" };
    }

    /// <summary>
    /// Walks from <paramref name="workingDirectory"/> up toward the filesystem root,
    /// collecting instruction files until reaching the repository root (marked by .git).
    /// Stops at the repository root (inclusive) or at <paramref name="workingDirectory"/> if no .git ancestor exists.
    /// Results are ordered filesystem-root-first, so working-directory-found files appear last.
    /// </summary>
    public async ValueTask<InstructionContext> ResolveAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var sources = new List<InstructionSource>();

        DirectoryInfo? current = new(Path.GetFullPath(workingDirectory));
        DirectoryInfo? repoRoot = null;
        var stack = new Stack<DirectoryInfo>();

        while (current is not null)
        {
            ct.ThrowIfCancellationRequested();

            stack.Push(current);

            if (Path.Exists(Path.Combine(current.FullName, ".git")))
            {
                repoRoot = current;
                break;
            }

            current = current.Parent;
        }

        while (stack.Count > 0)
        {
            DirectoryInfo dir = stack.Pop();
            InstructionSourceKind kind = repoRoot is not null && dir.FullName == repoRoot.FullName
                ? InstructionSourceKind.RepoRoot
                : InstructionSourceKind.Ancestor;

            foreach (string filename in this._fallbackFilenames)
            {
                string filePath = Path.Combine(dir.FullName, filename);
                if (!File.Exists(filePath))
                {
                    continue;
                }

                try
                {
                    string content = await File.ReadAllTextAsync(filePath, ct);
                    sources.Add(new InstructionSource(filePath, content, kind));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(CultureInfo.InvariantCulture,
                        $"Failed to read instruction file {filePath}: {ex.Message}");
                    continue;
                }

                break;
            }
        }

        return new InstructionContext(sources);
    }
}
