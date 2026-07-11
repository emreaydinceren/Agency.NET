using Agency.Harness.Console.Services;
using Agency.VectorStore.Common;
using Moq;

namespace Agency.Harness.Console.Test;

/// <summary>
/// Unit tests for <see cref="DocumentContextHydrationService"/>.
/// </summary>
public sealed class DocumentContextHydrationServiceTests
{
    private readonly Mock<IVectorStore> _storeMock = new();
    private readonly Mock<IProjectSessionState> _stateMock = new();

    /// <summary>
    /// Initializes the shared session-state mock used by every test in this fixture.
    /// </summary>
    public DocumentContextHydrationServiceTests()
    {
        _stateMock.Setup(s => s.UserId).Returns("user1");
        _stateMock.Setup(s => s.SessionId).Returns("sess-123");
        _stateMock.Setup(s => s.LoadedProjects).Returns(Array.Empty<string>());
    }

    private DocumentContextHydrationService CreateService() =>
        new(_storeMock.Object, _stateMock.Object);

    private void SetupListDocuments(params DocumentInfo[] docs)
    {
        _storeMock
            .Setup(s => s.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<DocumentInfo>)docs);
    }

    /// <summary>
    /// A freshly constructed service starts dirty, so the first refresh must query the store.
    /// </summary>
    [Fact]
    public async Task RefreshIfDirtyAsync_InitiallyDirty_QueriesStore()
    {
        SetupListDocuments();
        DocumentContextHydrationService service = CreateService();

        await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        _storeMock.Verify(
            s => s.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// When the store reports no documents, no context fact is produced.
    /// </summary>
    [Fact]
    public async Task RefreshIfDirtyAsync_NoDocuments_ReturnsNull()
    {
        SetupListDocuments();
        DocumentContextHydrationService service = CreateService();

        string? result = await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    /// <summary>
    /// A document scoped to every session and project is labeled <c>[global]</c> in the resulting fact.
    /// </summary>
    [Fact]
    public async Task RefreshIfDirtyAsync_WithGlobalDocument_ReturnsFactWithGlobalLabel()
    {
        SetupListDocuments(new DocumentInfo("docs/Home.md", "*", "*"));
        DocumentContextHydrationService service = CreateService();

        string? result = await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("[global]", result);
        Assert.Contains("docs/Home.md", result);
    }

    /// <summary>
    /// A document scoped to the current session is labeled <c>[session]</c> in the resulting fact.
    /// </summary>
    [Fact]
    public async Task RefreshIfDirtyAsync_WithSessionDocument_ReturnsFactWithSessionLabel()
    {
        SetupListDocuments(new DocumentInfo("src/README.md", "sess-123", "*"));
        DocumentContextHydrationService service = CreateService();

        string? result = await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("[session]", result);
        Assert.Contains("src/README.md", result);
    }

    /// <summary>
    /// A document scoped to a specific project is labeled <c>[project:&lt;name&gt;]</c> in the resulting fact.
    /// </summary>
    [Fact]
    public async Task RefreshIfDirtyAsync_WithProjectDocument_ReturnsFactWithProjectLabel()
    {
        SetupListDocuments(new DocumentInfo("docs/Arch.md", "*", "MyProj"));
        DocumentContextHydrationService service = CreateService();

        string? result = await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("[project:MyProj]", result);
        Assert.Contains("docs/Arch.md", result);
    }

    /// <summary>
    /// The result is cached: calling refresh again without an intervening <see cref="DocumentContextHydrationService.MarkDirty"/>
    /// must not re-query the store.
    /// </summary>
    [Fact]
    public async Task RefreshIfDirtyAsync_CalledTwiceWithoutMarkDirty_OnlyQueriesOnce()
    {
        SetupListDocuments(new DocumentInfo("docs/Home.md", "*", "*"));
        DocumentContextHydrationService service = CreateService();

        await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);
        await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        _storeMock.Verify(
            s => s.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Calling <see cref="DocumentContextHydrationService.MarkDirty"/> forces the next refresh to re-query the store.
    /// </summary>
    [Fact]
    public async Task MarkDirty_CausesNextCallToQueryAgain()
    {
        SetupListDocuments();
        DocumentContextHydrationService service = CreateService();

        await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);
        service.MarkDirty();
        await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        _storeMock.Verify(
            s => s.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
