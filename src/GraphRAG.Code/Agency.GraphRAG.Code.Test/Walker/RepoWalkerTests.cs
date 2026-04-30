using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Walker;

/// <summary>
/// Tests for <see cref="RepoWalker"/>.
/// </summary>
public sealed class RepoWalkerTests
{
    [Fact]
    public async Task WalkAsync_FirstIndex_ReturnsSupportedTrackedFilesInFullMode()
    {
        FakeGitState gitState = new()
        {
            HeadCommit = "head-1",
            IsShallowRepository = false,
            TrackedFiles = ["src/App.cs", "README.md", "src/app.ts"],
        };
        RepoWalker repoWalker = CreateRepoWalker(gitState);
        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = @"E:\Repos\Agency",
            IsShallow = false,
            IndexedCommit = null,
        };

        WalkResult result = await repoWalker.WalkAsync(repo, TestContext.Current.CancellationToken);

        Assert.Equal(WalkMode.Full, result.Mode);
        Assert.Equal("head-1", result.HeadCommit);
        Assert.Equal(["src/App.cs", "src/app.ts"], result.Files.Select(file => file.Path).ToArray());
        Assert.All(result.Files, file => Assert.Equal(WalkedFileStatus.Added, file.Status));
        Assert.Equal(1, gitState.LsFilesCallCount);
        Assert.Equal(0, gitState.DiffNameStatusCallCount);
    }

    [Fact]
    public async Task WalkAsync_IncrementalIndex_ReturnsAddedModifiedDeletedAndRenamedFiles()
    {
        FakeGitState gitState = new()
        {
            HeadCommit = "head-2",
            IsShallowRepository = false,
            IsAncestor = true,
            DiffEntries =
            [
                new GitDiffEntry('A', null, "src/NewFile.cs"),
                new GitDiffEntry('M', null, "src/Changed.ts"),
                new GitDiffEntry('D', "src/Removed.py", null),
                new GitDiffEntry('R', "src/OldName.jsx", "src/NewName.jsx"),
            ],
        };
        RepoWalker repoWalker = CreateRepoWalker(gitState);
        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = @"E:\Repos\Agency",
            IsShallow = false,
            IndexedCommit = "indexed-1",
        };

        WalkResult result = await repoWalker.WalkAsync(repo, TestContext.Current.CancellationToken);

        Assert.Equal(WalkMode.Incremental, result.Mode);
        Assert.Equal("head-2", result.HeadCommit);
        Assert.Collection(
            result.Files,
            file =>
            {
                Assert.Equal("src/NewFile.cs", file.Path);
                Assert.Equal(WalkedFileStatus.Added, file.Status);
            },
            file =>
            {
                Assert.Equal("src/Changed.ts", file.Path);
                Assert.Equal(WalkedFileStatus.Modified, file.Status);
            },
            file =>
            {
                Assert.Equal("src/Removed.py", file.Path);
                Assert.Equal(WalkedFileStatus.Deleted, file.Status);
            },
            file =>
            {
                Assert.Equal("src/NewName.jsx", file.Path);
                Assert.Equal("src/OldName.jsx", file.OldPath);
                Assert.Equal(WalkedFileStatus.Renamed, file.Status);
            });
        Assert.Equal(1, gitState.DiffNameStatusCallCount);
        Assert.Equal(0, gitState.LsFilesCallCount);
    }

    [Fact]
    public async Task WalkAsync_ShallowRepository_FallsBackToFullMode()
    {
        FakeGitState gitState = new()
        {
            HeadCommit = "head-3",
            IsShallowRepository = true,
            TrackedFiles = ["src/App.cs"],
        };
        RepoWalker repoWalker = CreateRepoWalker(gitState);
        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = @"E:\Repos\Agency",
            IsShallow = false,
            IndexedCommit = "indexed-2",
        };

        WalkResult result = await repoWalker.WalkAsync(repo, TestContext.Current.CancellationToken);

        Assert.Equal(WalkMode.Full, result.Mode);
        Assert.True(result.IsShallowRepository);
        Assert.Single(result.Files);
        Assert.Equal(1, gitState.LsFilesCallCount);
        Assert.Equal(0, gitState.DiffNameStatusCallCount);
        Assert.Equal(0, gitState.IsAncestorCallCount);
    }

    [Fact]
    public async Task WalkAsync_ForcePushDivergence_ReturnsRecoveryFullMode()
    {
        FakeGitState gitState = new()
        {
            HeadCommit = "head-4",
            IsShallowRepository = false,
            IsAncestor = false,
            TrackedFiles = ["src/App.cs", "src/Worker.ts"],
        };
        RepoWalker repoWalker = CreateRepoWalker(gitState);
        Repo repo = new()
        {
            Id = Guid.NewGuid(),
            LocalPath = @"E:\Repos\Agency",
            IsShallow = false,
            IndexedCommit = "indexed-3",
        };

        WalkResult result = await repoWalker.WalkAsync(repo, TestContext.Current.CancellationToken);

        Assert.Equal(WalkMode.RecoveryFull, result.Mode);
        Assert.Equal(["src/App.cs", "src/Worker.ts"], result.Files.Select(file => file.Path).ToArray());
        Assert.Equal(1, gitState.IsAncestorCallCount);
        Assert.Equal(1, gitState.LsFilesCallCount);
        Assert.Equal(0, gitState.DiffNameStatusCallCount);
    }

    private static RepoWalker CreateRepoWalker(FakeGitState gitState)
    {
        return new RepoWalker(
            _ =>
            {
                gitState.LsFilesCallCount++;
                return gitState.TrackedFiles;
            },
            _ => gitState.HeadCommit,
            _ => gitState.IsShallowRepository,
            (_, _, _) =>
            {
                gitState.IsAncestorCallCount++;
                return gitState.IsAncestor;
            },
            (_, _, _) =>
            {
                gitState.DiffNameStatusCallCount++;
                return gitState.DiffEntries;
            },
            path => FakeLanguageDetector(path));
    }

    private sealed class FakeGitState
    {
        public string HeadCommit { get; init; } = "head";

        public bool IsShallowRepository { get; init; }

        public bool IsAncestor { get; init; } = true;

        public IReadOnlyList<string> TrackedFiles { get; init; } = [];

        public IReadOnlyList<GitDiffEntry> DiffEntries { get; init; } = [];

        public int LsFilesCallCount { get; set; }

        public int DiffNameStatusCallCount { get; set; }

        public int IsAncestorCallCount { get; set; }
    }

    private static Language FakeLanguageDetector(string repositoryRelativePath) =>
        Path.GetExtension(repositoryRelativePath).ToLowerInvariant() switch
        {
            ".cs" => Language.CSharp,
            ".ts" => Language.TypeScript,
            ".tsx" => Language.Tsx,
            ".js" => Language.JavaScript,
            ".jsx" => Language.Jsx,
            ".py" => Language.Python,
            _ => Language.Unknown,
        };
}
