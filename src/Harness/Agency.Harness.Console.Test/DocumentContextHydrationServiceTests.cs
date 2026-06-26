using Agency.Harness;
using Agency.Harness.Console.Services;
using Agency.VectorStore.Common;
using Moq;

namespace Agency.Harness.Console.Test;

public sealed class DocumentContextHydrationServiceTests
{
    private readonly Mock<IVectorStore> _storeMock = new();
    private readonly Mock<IProjectSessionState> _stateMock = new();

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

    [Fact]
    public async Task RefreshIfDirtyAsync_NoDocuments_ReturnsNull()
    {
        SetupListDocuments();
        DocumentContextHydrationService service = CreateService();

        string? result = await service.RefreshIfDirtyAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

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
