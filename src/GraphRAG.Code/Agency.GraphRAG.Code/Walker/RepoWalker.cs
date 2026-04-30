using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Walker;

/// <summary>
/// Walks a git-backed repository to determine the files that should be indexed.
/// </summary>
public sealed class RepoWalker
{
    private readonly Func<string, IReadOnlyList<string>> listFiles;
    private readonly Func<string, string> revParseHead;
    private readonly Func<string, bool> isShallowRepository;
    private readonly Func<string, string, string, bool> isAncestor;
    private readonly Func<string, string, string, IReadOnlyList<GitDiffEntry>> diffNameStatus;
    private readonly Func<string, Language> detectLanguage;

    /// <summary>
    /// Initializes a new instance of the <see cref="RepoWalker"/> class.
    /// </summary>
    /// <param name="gitProcessRunner">The git runner used to inspect repository state.</param>
    public RepoWalker(GitProcessRunner gitProcessRunner)
        : this(
            gitProcessRunner.LsFiles,
            gitProcessRunner.RevParseHead,
            gitProcessRunner.IsShallowRepository,
            gitProcessRunner.IsAncestor,
            gitProcessRunner.DiffNameStatus,
            path => LanguageDetector.Detect(path))
    {
    }

    /// <summary>
    /// Computes the repository walk result for the provided repository.
    /// </summary>
    /// <param name="repo">The repository to inspect.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The files that should be processed for indexing.</returns>
    public Task<WalkResult> WalkAsync(Repo repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        cancellationToken.ThrowIfCancellationRequested();

        string headCommit = this.revParseHead(repo.LocalPath);
        bool isShallow = this.isShallowRepository(repo.LocalPath);

        if (string.IsNullOrWhiteSpace(repo.IndexedCommit) || isShallow)
        {
            return Task.FromResult(this.CreateFullResult(
                repo.LocalPath,
                WalkMode.Full,
                headCommit,
                isShallow));
        }

        bool indexedCommitIsAncestor = this.isAncestor(repo.LocalPath, repo.IndexedCommit, headCommit);
        if (!indexedCommitIsAncestor)
        {
            return Task.FromResult(this.CreateFullResult(repo.LocalPath, WalkMode.RecoveryFull, headCommit, isShallow));
        }

        IReadOnlyList<GitDiffEntry> diffEntries = this.diffNameStatus(
            repo.LocalPath,
            repo.IndexedCommit,
            headCommit);

        List<WalkedFile> files = [];
        foreach (GitDiffEntry diffEntry in diffEntries)
        {
            WalkedFile? walkedFile = this.TryCreateIncrementalFile(diffEntry);
            if (walkedFile is not null)
            {
                files.Add(walkedFile);
            }
        }

        return Task.FromResult(new WalkResult
        {
            Mode = WalkMode.Incremental,
            Files = files,
            HeadCommit = headCommit,
            IsShallowRepository = isShallow,
        });
    }

    internal RepoWalker(
        Func<string, IReadOnlyList<string>> listFiles,
        Func<string, string> revParseHead,
        Func<string, bool> isShallowRepository,
        Func<string, string, string, bool> isAncestor,
        Func<string, string, string, IReadOnlyList<GitDiffEntry>> diffNameStatus,
        Func<string, Language> detectLanguage)
    {
        this.listFiles = listFiles;
        this.revParseHead = revParseHead;
        this.isShallowRepository = isShallowRepository;
        this.isAncestor = isAncestor;
        this.diffNameStatus = diffNameStatus;
        this.detectLanguage = detectLanguage;
    }

    private WalkResult CreateFullResult(
        string repositoryPath,
        WalkMode mode,
        string headCommit,
        bool isShallow)
    {
        IReadOnlyList<string> trackedFiles = this.listFiles(repositoryPath);

        List<WalkedFile> files = [];
        foreach (string trackedFile in trackedFiles)
        {
            Language language = this.detectLanguage(trackedFile);
            if (language is Language.Unknown)
            {
                continue;
            }

            files.Add(new WalkedFile
            {
                Path = trackedFile,
                Status = WalkedFileStatus.Added,
                Language = language,
            });
        }

        return new WalkResult
        {
            Mode = mode,
            Files = files,
            HeadCommit = headCommit,
            IsShallowRepository = isShallow,
        };
    }

    private WalkedFile? TryCreateIncrementalFile(GitDiffEntry diffEntry)
    {
        string candidatePath = diffEntry.Status switch
        {
            'R' => diffEntry.NewPath ?? diffEntry.OldPath ?? string.Empty,
            'D' => diffEntry.OldPath ?? diffEntry.NewPath ?? string.Empty,
            _ => diffEntry.NewPath ?? string.Empty,
        };

        Language language = this.detectLanguage(candidatePath);
        if (language is Language.Unknown)
        {
            return null;
        }

        return new WalkedFile
        {
            Path = diffEntry.NewPath ?? diffEntry.OldPath ?? string.Empty,
            OldPath = diffEntry.OldPath,
            Status = diffEntry.Status switch
            {
                'A' => WalkedFileStatus.Added,
                'M' => WalkedFileStatus.Modified,
                'D' => WalkedFileStatus.Deleted,
                'R' => WalkedFileStatus.Renamed,
                _ => throw new InvalidOperationException($"Unsupported git status '{diffEntry.Status}'."),
            },
            Language = language,
        };
    }
}

/// <summary>
/// Represents the result of walking a repository for indexing changes.
/// </summary>
public sealed record class WalkResult
{
    /// <summary>Gets the walk mode that was used.</summary>
    public required WalkMode Mode { get; init; }

    /// <summary>Gets the files returned by the walk.</summary>
    public required IReadOnlyList<WalkedFile> Files { get; init; }

    /// <summary>Gets the current HEAD commit when available.</summary>
    public string? HeadCommit { get; init; }

    /// <summary>Gets a value indicating whether the repository was detected as shallow.</summary>
    public bool IsShallowRepository { get; init; }
}

/// <summary>
/// Represents a single file emitted by the repository walk.
/// </summary>
public sealed record class WalkedFile
{
    /// <summary>Gets the current path for the file relative to the repository root.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the previous path for renamed files.</summary>
    public string? OldPath { get; init; }

    /// <summary>Gets the file change status.</summary>
    public required WalkedFileStatus Status { get; init; }

    /// <summary>Gets the detected language name for the file.</summary>
    public Language Language { get; init; }
}

/// <summary>
/// Indicates how the repository should be indexed.
/// </summary>
public enum WalkMode
{
    /// <summary>Index the full repository from tracked files.</summary>
    Full,

    /// <summary>Index only files returned by git diff.</summary>
    Incremental,

    /// <summary>Recover from rewritten history by forcing a full re-index.</summary>
    RecoveryFull,
}

/// <summary>
/// Describes the type of file change observed by the repository walker.
/// </summary>
public enum WalkedFileStatus
{
    /// <summary>A file was added.</summary>
    Added,

    /// <summary>A file was modified.</summary>
    Modified,

    /// <summary>A file was deleted.</summary>
    Deleted,

    /// <summary>A file was renamed.</summary>
    Renamed,
}

