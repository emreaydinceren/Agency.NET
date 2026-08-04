using Agency.Harness.Console.Commands;
using Agency.Harness.Console.Services;
using Agency.Harness.Contexts;
using Agency.VectorStore.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Agency.Harness.Console.Test.Commands;

/// <summary>
/// Unit tests pinning the argument-extraction hazard described in Spec §6.4.2: <see cref="ProjectsCommand"/>
/// slices a command's argument out of the raw input string using a hard-coded literal command-text length
/// (e.g. <c>input["/projects-load".Length..]</c>). Task 20 renamed the commands registered in
/// <see cref="CommandRegistry"/> from the plural <c>/projects-*</c> spelling to the singular <c>/project-*</c>
/// spelling, but left the internal slicing literals in <see cref="ProjectsCommand.LoadAsync"/> and
/// <see cref="ProjectsCommand.UnloadAsync"/> pointing at the old, now-stale, plural literals. Because the two
/// can silently drift, this theory pins the exact argument that must reach the downstream
/// <see cref="IProjectSessionState"/> call.
/// </summary>
/// <remarks>
/// Scoped to <c>LoadAsync</c>/<c>UnloadAsync</c> only: <c>CreateAsync</c>, <c>DeleteAsync</c>, and
/// <c>ShowAsync</c> are currently placeholder stubs (added by Task 20) that ignore their <c>input</c>
/// parameter entirely — they have no slicing logic yet to pin a hazard against. Once Tasks 24, 30, and 32
/// give those three commands real argument-extraction bodies, this theory should be widened to cover all
/// five argument-taking project commands, per the original Task 21 scope.
/// </remarks>
// This class (along with McpCommandTests and ConsolePickerTests) drives Spectre.Console commands by
// temporarily swapping the process-wide static AnsiConsole.Console for a captured instance, then
// restoring it. That static is shared across the whole test process, so xUnit's default cross-class
// parallelization lets two test classes race on it: one class's captured writer can observe another
// class's output (or vice versa), producing flaky, non-deterministic failures unrelated to the
// behavior under test. Sharing the "AnsiConsoleTests" collection serializes exactly the classes that
// touch the static, rather than disabling parallelization for the whole assembly.
[Collection("AnsiConsoleTests")]
public sealed class ProjectsCommandTests
{
    // ---------------------------------------------------------------------------
    // Minimal test doubles
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A trivial <see cref="IServiceProvider"/> resolving from a fixed set of pre-built instances —
    /// enough for <see cref="ProjectsCommand.LoadAsync"/>/<see cref="ProjectsCommand.UnloadAsync"/>,
    /// which only resolve <see cref="IProjectSessionState"/> and <see cref="DocumentContextHydrationService"/>.
    /// </summary>
    private sealed class FakeServiceProvider(params (Type Type, object Instance)[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            services.FirstOrDefault(s => s.Type == serviceType).Instance;
    }

    /// <summary>No-op <see cref="IChatOutput"/> so <c>AnsiConsole.MarkupLine</c> calls inside the command
    /// bodies don't need a real console output sink to be observed.</summary>
    private sealed class NoOpChatOutput : IChatOutput
    {
        public void WriteLine() { }
        public void WriteLine(string? colorName, string text) { }
        public void WriteLine(string text) { }
        public void Write(string? colorName, string text) { }
        public void Write(string text) { }
        public void WriteLineMarkdown(string text) { }
        public void WriteMarkup(string text) { }
        public void WriteLineMarkup(string text) { }
        public void StartSpinner(string markup = "[yellow]Thinking...[/]") { }
        public void StopSpinner() { }
        public void WriteMarkdownInBorderedPanel(string header, string text) { }
    }

    /// <summary>
    /// Builds a real <see cref="ConsoleChatSession"/> — <see cref="ProjectsCommand.LoadAsync"/>/
    /// <see cref="ProjectsCommand.UnloadAsync"/>/<see cref="ProjectsCommand.CreateAsync"/> only ever touch
    /// its <c>ServiceProvider</c>, but the parameter type is the concrete session class, not an interface,
    /// so a minimal-but-real instance is constructed rather than faked.
    /// <see cref="Agency.Harness.Contexts.ToolContext.Empty"/> and
    /// <see cref="Agency.Harness.Contexts.SkillContext.Empty"/> keep the rest of construction trivial; the
    /// <see cref="IChatClient"/> behind <see cref="Agent"/> is never invoked because these tests never
    /// start a turn.
    /// </summary>
    /// <param name="projectSessionState">The fake/mock <see cref="IProjectSessionState"/> to resolve.</param>
    /// <param name="vectorStoreMock">
    /// The <see cref="IVectorStore"/> mock to resolve — shared with the <see cref="DocumentContextHydrationService"/>
    /// constructed alongside it, so a caller can configure <c>CreateProjectAsync</c>/<c>ListDocumentsAsync</c>
    /// before or after this call. Defaults to a fresh, unconfigured mock.
    /// </param>
    private static ConsoleChatSession CreateSession(
        IProjectSessionState projectSessionState,
        Mock<IVectorStore>? vectorStoreMock = null)
    {
        vectorStoreMock ??= new Mock<IVectorStore>();
        var hydration = new DocumentContextHydrationService(vectorStoreMock.Object, projectSessionState);

        var serviceProvider = new FakeServiceProvider(
            (typeof(IProjectSessionState), projectSessionState),
            (typeof(IVectorStore), vectorStoreMock.Object),
            (typeof(DocumentContextHydrationService), hydration));

        var chatClientMock = new Mock<IChatClient>();
        var agent = new Agent(chatClientMock.Object, "test-model");

        return new ConsoleChatSession(
            serviceProvider,
            agent,
            Options.Create(new AgentOptions()),
            ToolContext.Empty,
            new NoOpChatOutput(),
            SkillContext.Empty);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the static <see cref="AnsiConsole.Console"/> swapped for a
    /// string-backed instance, so plain text written via <c>AnsiConsole.MarkupLine</c> (as
    /// <see cref="ProjectsCommand.CreateAsync"/> does) can be asserted on directly. Markup tags (e.g.
    /// <c>[green]...[/]</c>) are consumed by Spectre's markup parser regardless of
    /// <see cref="ColorSystemSupport.NoColors"/>, so the captured text is the plain message with no color
    /// codes — see <c>MarkdownRendererTableTests.Render</c> for the same pattern applied to a single
    /// renderable rather than the global console.
    /// </summary>
    private static async Task<string> RunCapturingOutputAsync(Func<Task> action)
    {
        var writer = new StringWriter();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        // A StringWriter-backed output isn't a real terminal, so Spectre can't detect a window
        // width and falls back to 80 columns — narrow enough that a single long status line (e.g.
        // ShowAsync's empty-state message) gets hard-wrapped mid-word. Widen just this test
        // instance's profile so exact-match assertions on such lines aren't wrap-dependent; this
        // is scoped to `captured`, never the shared static AnsiConsole.Console/Profile.
        captured.Profile.Width = 200;

        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = captured;
        try
        {
            await action();
        }
        finally
        {
            AnsiConsole.Console = original;
        }

        return writer.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the static <see cref="AnsiConsole.Console"/> swapped for a
    /// <see cref="TestConsole"/> — the Spectre.Console.Testing double, already a transitive dependency of
    /// this project via <c>Agency.Harness.Console</c>'s own <c>Spectre.Console</c> reference but not
    /// previously pulled in as a direct <c>PackageReference</c> (added alongside this test suite).
    /// <see cref="RunCapturingOutputAsync"/> swaps in a plain string-writer-backed console, which is
    /// enough for commands that only *write* (e.g. <c>CreateAsync</c>); <c>DeleteAsync</c> additionally
    /// *reads* — its confirmation gate calls <c>AnsiConsole.Confirm</c>, which blocks on
    /// <see cref="IAnsiConsole.Input"/> — so a console with no input queue cannot drive it at all.
    /// <see cref="TestConsole.Input"/> lets a test queue the exact keystrokes a human would type at the
    /// <c>[y/N]</c> prompt. When <paramref name="confirmResponse"/> is <see langword="null"/>, nothing is
    /// queued: if the command under test calls <c>AnsiConsole.Confirm</c> anyway, reading from the empty
    /// queue throws — which is exactly the failure signature
    /// <see cref="DeleteCommand_UnknownProject_PrintsNotFound_AndDoesNotPrompt"/> needs, since the whole
    /// point of that test is that the confirm prompt must never be reached.
    /// </summary>
    private static async Task<string> RunWithConfirmAsync(Func<Task> action, string? confirmResponse)
    {
        var console = new TestConsole();

        // Same rationale as RunCapturingOutputAsync: TestConsole isn't a real terminal, so Spectre
        // falls back to an 80-column wrap width. Widen this instance so long single-line messages
        // aren't hard-wrapped, scoped to this test's own console.
        console.Profile.Width = 200;

        if (confirmResponse is not null)
        {
            console.Input.PushTextWithEnter(confirmResponse);
        }

        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        try
        {
            await action();
        }
        finally
        {
            AnsiConsole.Console = original;
        }

        return console.Output.TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Drives the session's <see cref="DocumentContextHydrationService"/> to a known-clean state
    /// (<c>IsDirty == false</c>) and returns it so a later <c>MarkDirty()</c> call inside
    /// <see cref="ProjectsCommand.CreateAsync"/> becomes observable as a false-to-true transition.
    /// </summary>
    /// <remarks>
    /// <see cref="DocumentContextHydrationService"/> is <c>internal sealed</c> with a non-virtual
    /// <c>MarkDirty()</c> and no interface, so Moq cannot generate a proxy for it (a sealed type with no
    /// virtual members cannot be intercepted) — mocking it, per Task 23's "Read first" list, is not an
    /// option without an interface extraction, which is explicitly out of scope for this task. Its own
    /// <see cref="DocumentContextHydrationService.IsDirty"/> property is already public and exists for
    /// exactly this kind of external observation, so it is used as the spy instead: prime the real
    /// instance clean via <see cref="DocumentContextHydrationService.RefreshIfDirtyAsync"/> (backed by an
    /// empty-list <c>ListDocumentsAsync</c> stub so priming can't throw or itself get flagged dirty), then
    /// read <c>IsDirty</c> after the command runs. Because <c>MarkDirty()</c> only ever sets a bool to
    /// <see langword="true"/>, "was it called" and "was it called exactly once" are indistinguishable from
    /// the outside — idempotent by construction — so asserting the transition is precise, not an
    /// approximation.
    /// </remarks>
    private static async Task<DocumentContextHydrationService> GetCleanHydrationAsync(
        ConsoleChatSession session,
        Mock<IVectorStore> vectorStoreMock)
    {
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentInfo>());

        DocumentContextHydrationService hydration =
            session.ServiceProvider.GetRequiredService<DocumentContextHydrationService>();
        await hydration.RefreshIfDirtyAsync();
        Assert.False(hydration.IsDirty); // sanity: primed clean before the command under test runs

        return hydration;
    }

    // ---------------------------------------------------------------------------
    // 6.4.2 — argument-extraction length-coupling guard
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Pins Spec §6.4.2's dispatch hazard: <c>ProjectsCommand.LoadAsync</c>/<c>UnloadAsync</c> slice the
    /// argument out of the raw input using a hard-coded command-text length literal. That literal must
    /// track whatever command text is actually registered in <see cref="CommandRegistry"/> — if the two
    /// drift (as they did when Task 20 renamed the registered command but not the slice literal), the
    /// argument reaching <see cref="IProjectSessionState"/> is silently corrupted instead of matching what
    /// the operator typed.
    /// </summary>
    /// <remarks>
    /// Includes both the space-delimited form an operator actually types (<c>/project-load handbook</c>)
    /// and a no-space form (<c>/project-loadhandbook</c>). Both must extract <c>"handbook"</c>, but only
    /// the no-space cases currently fail: the stale literals (<c>"/projects-load"</c>/<c>"/projects-unload"</c>,
    /// 14/16 chars) are exactly <b>one</b> character longer than the actual registered command text
    /// (13/15 chars), and with a space delimiter present, over-slicing by exactly one character only
    /// consumes that delimiter space — which <c>.Trim()</c> would have removed anyway — so the space-delimited
    /// cases pass today by coincidence, not because the extraction is correct. The no-space cases have no
    /// delimiter to absorb the off-by-one, so the extra character eaten comes out of the argument itself,
    /// reproducing Spec §6.4.2's documented symptom exactly ("handbook" → "andbook"). Both forms are kept
    /// so Task 22's fix is proven not to special-case the delimiter — a single shared-constant slice must
    /// get both right.
    /// </remarks>
    [Theory]
    [InlineData("load", "/project-load handbook")]
    [InlineData("load", "/project-loadhandbook")]
    [InlineData("unload", "/project-unload handbook")]
    [InlineData("unload", "/project-unloadhandbook")]
    public async Task ProjectsCommand_ArgumentExtraction_UsesRegisteredCommandLength(string command, string input)
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        string? capturedArgument = null;

        ConsoleChatSession session = CreateSession(stateMock.Object);

        if (command == "load")
        {
            stateMock.Setup(s => s.LoadProject(It.IsAny<string>()))
                .Callback<string>(name => capturedArgument = name);
            await ProjectsCommand.LoadAsync(input, session);
        }
        else
        {
            stateMock.Setup(s => s.UnloadProject(It.IsAny<string>()))
                .Callback<string>(name => capturedArgument = name);
            await ProjectsCommand.UnloadAsync(input, session);
        }

        Assert.Equal("handbook", capturedArgument);
    }

    // ---------------------------------------------------------------------------
    // 6.4.3 / 8.2 — ProjectsCommand.CreateAsync ("/project-create") behaviour suite
    //
    // These tests target real create-and-activate behaviour (Spec §6.4.3, decision table §8.2).
    // ProjectsCommand.CreateAsync is currently a Task-20 placeholder stub that ignores its input and
    // does nothing, so every assertion below is expected to fail until Task 24 implements it.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Spec §8.2 row 1: a brand-new project. The store declares it (<c>CreateProjectAsync</c> → <c>true</c>),
    /// the REPL prints the "created" wording, and the project is loaded into the session.
    /// </summary>
    [Fact]
    public async Task CreateCommand_NewName_PrintsCreatedAndLoaded_AndLoadsProject()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.CreateProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(
            () => ProjectsCommand.CreateAsync("/project-create x", session));

        Assert.Equal("Project 'x' created and loaded.", output);
        stateMock.Verify(s => s.LoadProject("x"), Times.Once);
    }

    /// <summary>
    /// Spec §8.2 rows 2–4: the project already exists (declared or derived from content). The store
    /// reports <c>false</c>, the REPL prints the "already exists" wording, but the project is still
    /// loaded — §6.4.3's "why the already-exists path also loads".
    /// </summary>
    [Fact]
    public async Task CreateCommand_ExistingName_PrintsAlreadyExistsLoaded_AndLoadsProject()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.CreateProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(
            () => ProjectsCommand.CreateAsync("/project-create x", session));

        Assert.Equal("Project 'x' already exists — loaded.", output);
        stateMock.Verify(s => s.LoadProject("x"), Times.Once);
    }

    /// <summary>
    /// Spec §6.4.3 flow: an invalid raw name (<c>"*"</c> is reserved for the global scope, per
    /// <see cref="ProjectName.TryNormalize"/>) short-circuits before the store or the session are touched
    /// at all — neither <c>CreateProjectAsync</c> nor <c>LoadProject</c> is ever called.
    /// </summary>
    [Fact]
    public async Task CreateCommand_InvalidName_PrintsReason_AndDoesNotCallStore_AndDoesNotLoad()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(
            () => ProjectsCommand.CreateAsync("/project-create *", session));

        Assert.Equal("Invalid project name. '*' is reserved for the global scope.", output);
        vectorStoreMock.Verify(
            v => v.CreateProjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        stateMock.Verify(s => s.LoadProject(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Spec §8.2 rows 1–4 (not already loaded): loading changes <c>LoadedProjects</c>, so the inventory
    /// must be invalidated — the same rule <c>/project-load</c> already follows.
    /// </summary>
    [Fact]
    public async Task CreateCommand_ProjectNotAlreadyLoaded_LoadsIt_AndMarksDirty()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.SessionId).Returns("s1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.CreateProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);
        DocumentContextHydrationService hydration = await GetCleanHydrationAsync(session, vectorStoreMock);

        await RunCapturingOutputAsync(() => ProjectsCommand.CreateAsync("/project-create x", session));

        stateMock.Verify(s => s.LoadProject("x"), Times.Once);
        Assert.True(hydration.IsDirty);
    }

    /// <summary>
    /// Spec §8.2 row 5: the project is already loaded (exact spelling, or a mixed-case legacy spelling
    /// matched case-insensitively). <c>LoadProject</c> stays a no-op from the loaded-set's point of view,
    /// so <c>MarkDirty()</c> must never fire — this is what makes <c>/project-create</c> outcome-idempotent
    /// on a second run.
    /// </summary>
    [Theory]
    [InlineData("x")]
    [InlineData("X")]
    public async Task CreateCommand_ProjectAlreadyLoaded_IsFullyIdempotent(string preLoadedSpelling)
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.SessionId).Returns("s1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([preLoadedSpelling]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.CreateProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);
        DocumentContextHydrationService hydration = await GetCleanHydrationAsync(session, vectorStoreMock);

        await RunCapturingOutputAsync(() => ProjectsCommand.CreateAsync("/project-create x", session));

        stateMock.Verify(s => s.LoadProject("x"), Times.Once);
        Assert.False(hydration.IsDirty);
    }

    /// <summary>
    /// Regression guard for the superseded "create never marks dirty" rule (Spec §8.2, §14.4): a project
    /// that already exists <em>and already has documents</em>, but was not loaded in this session yet.
    /// Loading it changes <c>LoadedProjects</c>, so the P4 inventory must be invalidated even though the
    /// store reports the project as already existing — do not remove this test.
    /// </summary>
    [Fact]
    public async Task CreateCommand_ExistingPopulatedProject_MarksDirty()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.SessionId).Returns("s1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.CreateProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);
        DocumentContextHydrationService hydration = await GetCleanHydrationAsync(session, vectorStoreMock);

        await RunCapturingOutputAsync(() => ProjectsCommand.CreateAsync("/project-create x", session));

        Assert.True(hydration.IsDirty);
    }

    // ---------------------------------------------------------------------------
    // 8.1 Step 3 — ProjectsCommand.Resolve name-resolution helper suite
    //
    // ProjectsCommand.Resolve is a not-yet-implemented (Task 26) internal helper shared by
    // /project-delete and /project-show, resolving operator input against the known project ids
    // per Spec §8.1 Step 3. The current stub throws NotImplementedException, so every test below
    // is expected to fail via that exception until Task 26 implements it.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Spec §8.1 Step 3: an exact (ordinal) match against <c>knownProjects</c> resolves directly.
    /// </summary>
    [Fact]
    public void Resolve_ExactMatch_ReturnsStoredId()
    {
        ProjectsCommand.ResolveResult result = ProjectsCommand.Resolve("handbook", ["handbook"]);

        var known = Assert.IsType<ProjectsCommand.ResolveResult.Known>(result);
        Assert.Equal("handbook", known.StoredId);
    }

    /// <summary>
    /// Spec §8.1 Step 3: pre-existing stores may hold mixed-case ids created before the
    /// canonicalisation rule existed. A case-insensitive match must return the <em>stored</em>
    /// spelling ("Handbook"), not the canonicalised form ("handbook") — this is the legacy-data
    /// guarantee the resolution step exists for.
    /// </summary>
    [Fact]
    public void Resolve_CaseDifferentLegacyId_ReturnsStoredSpelling()
    {
        ProjectsCommand.ResolveResult result = ProjectsCommand.Resolve("handbook", ["Handbook"]);

        var known = Assert.IsType<ProjectsCommand.ResolveResult.Known>(result);
        Assert.Equal("Handbook", known.StoredId);
    }

    /// <summary>
    /// Spec §8.1 Step 3: a name that matches nothing in <c>knownProjects</c> (neither ordinal nor
    /// case-insensitive) resolves to <c>NotKnown</c> carrying the canonical form — the spelling
    /// <c>/project-create</c> would declare.
    /// </summary>
    [Fact]
    public void Resolve_UnknownName_ReturnsNotKnownWithCanonicalForm()
    {
        ProjectsCommand.ResolveResult result = ProjectsCommand.Resolve("newproject", []);

        var notKnown = Assert.IsType<ProjectsCommand.ResolveResult.NotKnown>(result);
        Assert.Equal("newproject", notKnown.Canonical);
    }

    /// <summary>
    /// Spec §8.1 Step 3: input that fails <see cref="ProjectName.TryNormalize"/> (here, the
    /// reserved global sentinel <c>"*"</c>) short-circuits to <c>Invalid</c> before any comparison
    /// against <c>knownProjects</c>, carrying the validation reason.
    /// </summary>
    [Fact]
    public void Resolve_InvalidName_ReturnsInvalidWithReason()
    {
        ProjectsCommand.ResolveResult result = ProjectsCommand.Resolve("*", []);

        var invalid = Assert.IsType<ProjectsCommand.ResolveResult.Invalid>(result);
        Assert.False(string.IsNullOrEmpty(invalid.Reason));
    }

    // ---------------------------------------------------------------------------
    // 6.4.4 / 6.4.5 — ProjectsCommand.GetProjectDocumentsAsync blast-radius helper suite
    //
    // ProjectsCommand.GetProjectDocumentsAsync is a not-yet-implemented (Task 28) internal helper
    // shared by /project-delete's confirmation count and /project-show's rendering: it calls
    // store.ListDocumentsAsync(userId, sessionId: null, projectIds: [projectId]) — which resolves
    // sessionId: null to the global sentinel, so the union returns global ∪ this project — then
    // filters back down to documents strictly tagged to projectId (Spec §6.4.4 "On the blast-radius
    // probe", §6.4.5 "No new store API"). The current stub throws NotImplementedException, so every
    // test below is expected to fail via that exception until Task 28 implements it.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Spec §6.4.4: passing <c>sessionId: null</c> resolves to the global sentinel, so the store's
    /// union includes global-scoped rows (<c>ProjectId == "*"</c>) alongside the target project's own
    /// rows. The helper's <c>Where</c> clause must strip those global rows back out.
    /// </summary>
    [Fact]
    public async Task GetProjectDocumentsAsync_GlobalScopedDocument_IsExcluded()
    {
        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1",
                null,
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentInfo>
            {
                new("project-doc.md", "*", "x"),
                new("global-doc.md", "*", "*"),
            });

        IReadOnlyList<DocumentInfo> result =
            await ProjectsCommand.GetProjectDocumentsAsync(vectorStoreMock.Object, "u1", "x", CancellationToken.None);

        Assert.DoesNotContain(result, d => d.ProjectId == "*");
    }

    /// <summary>
    /// Spec §6.4.4: a document strictly tagged to the target project survives the filter — this is
    /// what makes the helper's result "the documents in this project" rather than an empty set.
    /// </summary>
    [Fact]
    public async Task GetProjectDocumentsAsync_ProjectScopedDocument_IsIncluded()
    {
        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1",
                null,
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentInfo>
            {
                new("project-doc.md", "*", "x"),
                new("global-doc.md", "*", "*"),
            });

        IReadOnlyList<DocumentInfo> result =
            await ProjectsCommand.GetProjectDocumentsAsync(vectorStoreMock.Object, "u1", "x", CancellationToken.None);

        Assert.Contains(result, d => d.SourceFile == "project-doc.md" && d.ProjectId == "x");
    }

    /// <summary>
    /// Spec §6.4.4: the helper must call <c>ListDocumentsAsync</c> with <c>sessionId: null</c> (the
    /// global-sentinel resolution the union depends on) and <c>projectIds: [projectId]</c> — not, say,
    /// the caller's own session id or an empty project filter.
    /// </summary>
    [Fact]
    public async Task GetProjectDocumentsAsync_CallsListDocumentsAsync_WithNullSessionAndProjectIdFilter()
    {
        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentInfo>());

        await ProjectsCommand.GetProjectDocumentsAsync(vectorStoreMock.Object, "u1", "x", CancellationToken.None);

        vectorStoreMock.Verify(
            v => v.ListDocumentsAsync(
                "u1",
                null,
                It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == "x"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---------------------------------------------------------------------------
    // 6.4.4 / 8.3 / 8.5 — ProjectsCommand.DeleteAsync ("/project-delete") behaviour suite
    //
    // ProjectsCommand.DeleteAsync is currently a Task-20 placeholder stub that ignores its input and
    // does nothing, so every assertion below is expected to fail until Task 30 implements it.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Configures <paramref name="vectorStoreMock"/>'s <c>ListDocumentsAsync</c> to answer any call (the
    /// blast-radius probe <c>DeleteAsync</c> issues via <c>GetProjectDocumentsAsync</c>, and the priming
    /// call <see cref="DocumentContextHydrationService.RefreshIfDirtyAsync"/> issues) with an empty result,
    /// so tests that don't care about the reported document count don't need to set it up themselves —
    /// an unconfigured <c>Mock&lt;IVectorStore&gt;</c> call returns a <see langword="null"/> <c>Task</c>,
    /// which would throw a <see cref="NullReferenceException"/> the moment either caller awaits it.
    /// </summary>
    private static void SetupEmptyDocuments(Mock<IVectorStore> vectorStoreMock) =>
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentInfo>());

    /// <summary>
    /// Spec §8.3 step 4 / §6.4.6 (6.4.8): declining the <c>[y/N]</c> confirmation must leave the store
    /// untouched — nothing has been written at the point of decline (§8.3: "Nothing has been written at
    /// this point").
    /// </summary>
    [Fact]
    public async Task DeleteCommand_Declined_DoesNotCallDeleteProjectAsync()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        SetupEmptyDocuments(vectorStoreMock);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        await RunWithConfirmAsync(
            () => ProjectsCommand.DeleteAsync("/project-delete x", session),
            confirmResponse: "n");

        vectorStoreMock.Verify(
            v => v.DeleteProjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Spec §8.3 steps 5–8: confirming the prompt calls <c>DeleteProjectAsync</c> with the resolved id and
    /// marks the P4 inventory dirty unconditionally (documents left scope regardless of whether the
    /// project was loaded in this session).
    /// </summary>
    [Fact]
    public async Task DeleteCommand_Confirmed_CallsDeleteProjectAsync_AndMarksDirty()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.SessionId).Returns("s1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock.Setup(v => v.DeleteProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        SetupEmptyDocuments(vectorStoreMock);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);
        DocumentContextHydrationService hydration = await GetCleanHydrationAsync(session, vectorStoreMock);

        await RunWithConfirmAsync(
            () => ProjectsCommand.DeleteAsync("/project-delete x", session),
            confirmResponse: "y");

        vectorStoreMock.Verify(v => v.DeleteProjectAsync("u1", "x", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(hydration.IsDirty);
    }

    /// <summary>
    /// Spec §8.5 (chosen policy, step 1): if the deleted project is loaded in *this* session, it is
    /// auto-unloaded and the operator is told — "the session's scope now matches reality".
    /// </summary>
    [Fact]
    public async Task DeleteCommand_ProjectLoaded_UnloadsItAndNotifies()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns(["x"]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock.Setup(v => v.DeleteProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        SetupEmptyDocuments(vectorStoreMock);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunWithConfirmAsync(
            () => ProjectsCommand.DeleteAsync("/project-delete x", session),
            confirmResponse: "y");

        stateMock.Verify(s => s.UnloadProject("x"), Times.Once);
        Assert.Contains("was loaded in this session and has been unloaded", output);
    }

    /// <summary>
    /// Spec §8.5 (chosen policy, step 3): if the project is *not* loaded in this session,
    /// <c>UnloadProject</c> must never be called — there is no local scope to reconcile. (Other sessions
    /// that do have it loaded are deliberately left untouched; §8.5 explains why that degrades gracefully.)
    /// </summary>
    [Fact]
    public async Task DeleteCommand_ProjectNotLoaded_DoesNotTouchSessionState()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock.Setup(v => v.DeleteProjectAsync("u1", "x", It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        SetupEmptyDocuments(vectorStoreMock);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        await RunWithConfirmAsync(
            () => ProjectsCommand.DeleteAsync("/project-delete x", session),
            confirmResponse: "y");

        stateMock.Verify(s => s.UnloadProject(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Spec §8.3 step 2 / §6.4.6 (6.4.11): an unknown project prints "not found" and — critically —
    /// never shows the confirmation prompt at all ("asking someone to confirm deleting something that
    /// does not exist trains them to hit <c>y</c> reflexively"). No confirm response is queued in
    /// <see cref="TestConsole"/>'s input here; if <c>DeleteAsync</c> called <c>AnsiConsole.Confirm</c>
    /// regardless, reading from the empty queue would throw and this test would fail — that failure mode
    /// is the enforcement mechanism for "never prompts".
    /// </summary>
    [Fact]
    public async Task DeleteCommand_UnknownProject_PrintsNotFound_AndDoesNotPrompt()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        SetupEmptyDocuments(vectorStoreMock);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunWithConfirmAsync(
            () => ProjectsCommand.DeleteAsync("/project-delete x", session),
            confirmResponse: null);

        Assert.Equal("Project 'x' not found.", output);
        vectorStoreMock.Verify(
            v => v.DeleteProjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Spec §8.1 Step 3 / §6.4.6 (6.4.12): <c>/project-delete HandBook</c> must resolve to and delete the
    /// stored <c>"handbook"</c> — exercising <see cref="ProjectsCommand.Resolve"/> from inside the full
    /// command, not just in isolation (Task 25/26's own tests already cover the helper directly).
    /// </summary>
    [Fact]
    public async Task DeleteCommand_MixedCaseInput_ResolvesToStoredId()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "handbook" });
        vectorStoreMock.Setup(v => v.DeleteProjectAsync("u1", "handbook", It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        SetupEmptyDocuments(vectorStoreMock);

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        await RunWithConfirmAsync(
            () => ProjectsCommand.DeleteAsync("/project-delete HandBook", session),
            confirmResponse: "y");

        vectorStoreMock.Verify(v => v.DeleteProjectAsync("u1", "handbook", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------------------------------------------------------------------------
    // 6.4.6 (6.4.13) / L1 — ProjectsCommand.ListAsync regression guard
    //
    // Belongs to /project-list, not /project-delete, but Spec §6.4.6 groups it as 6.4.13 alongside the
    // delete suite. ListAsync already renders whatever ListProjectsAsync returns (Deliverable 3 widened
    // the underlying store query to declared ∪ derived projects), so this test is expected to pass
    // immediately — it pins L1 (existence is declared or derived, never contradicted) at the UI layer as
    // a permanent regression guard, not a red-phase assertion.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A project that is only *declared* (a registry row, zero chunks) must still appear in
    /// <c>/project-list</c>'s table — otherwise a brand-new, empty project would be invisible in the one
    /// place an operator checks what exists.
    /// </summary>
    [Fact]
    public async Task ListCommand_IncludesDeclaredEmptyProjects()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "empty-project" });

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ListAsync(session));

        Assert.Contains("empty-project", output);
    }

    // ---------------------------------------------------------------------------
    // 6.4.5 / 14.4a — ProjectsCommand.ShowAsync ("/project-show") behaviour suite
    //
    // ProjectsCommand.ShowAsync is currently a Task-20 placeholder stub that ignores its input and
    // returns Continue without doing anything, so every assertion below is expected to fail until
    // Task 32 implements it.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Spec §6.4.5 output format: a project with documents renders one table row per document, using the
    /// same <c>new Table().Border(TableBorder.Rounded)</c> construction as <c>/project-list</c>.
    /// </summary>
    [Fact]
    public async Task ShowCommand_ProjectWithDocuments_RendersOneRowPerDocument()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1", null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentInfo>
            {
                new("alpha.md", "*", "x"),
                new("beta.md", "*", "x"),
            });

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show x", session));

        Assert.Contains("alpha.md", output);
        Assert.Contains("beta.md", output);
    }

    /// <summary>
    /// Spec §6.4.5 "No new store API" / Task 28: the fake store returns one document strictly scoped to
    /// the project and one global-scoped document (<c>ProjectId == "*"</c>); only the former is rendered.
    /// Pins the <see cref="ProjectsCommand.GetProjectDocumentsAsync"/> filter at the command layer.
    /// </summary>
    [Fact]
    public async Task ShowCommand_ExcludesGlobalScopedDocuments()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1", null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentInfo>
            {
                new("project-doc.md", "*", "x"),
                new("global-doc.md", "*", "*"),
            });

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show x", session));

        Assert.Contains("project-doc.md", output);
        Assert.DoesNotContain("global-doc.md", output);
    }

    /// <summary>
    /// Spec §6.4.5 / §14.4a's second decision: a declared-but-empty project prints an explicit sentence
    /// instead of an empty table — "a borderless void where a table should be reads as a bug".
    /// </summary>
    [Fact]
    public async Task ShowCommand_DeclaredButEmptyProject_PrintsEmptyStateMessage_AndRendersNoTable()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1", null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentInfo>());

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show x", session));

        Assert.Equal(
            "Project 'x' has no documents ingested yet. Use /add-file or /add-folder to ingest into it.",
            output);
        Assert.DoesNotContain("Document", output); // no table column header ⇒ no table was rendered
    }

    /// <summary>
    /// Spec §6.4.5 flow: an unknown project prints "not found" — identical wording to
    /// <c>/project-delete</c>'s known-set check.
    /// </summary>
    [Fact]
    public async Task ShowCommand_UnknownProject_PrintsNotFound()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show x", session));

        Assert.Equal("Project 'x' not found.", output);
    }

    /// <summary>
    /// Spec §6.4.5 flow ("Validate before resolving the store"): an invalid raw name short-circuits
    /// before the documents are ever fetched — <c>ListDocumentsAsync</c> (the call underlying
    /// <see cref="ProjectsCommand.GetProjectDocumentsAsync"/>) must never be reached.
    /// </summary>
    [Fact]
    public async Task ShowCommand_InvalidName_PrintsReason_AndDoesNotCallStore()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show *", session));

        Assert.Equal("Invalid project name. '*' is reserved for the global scope.", output);
        vectorStoreMock.Verify(
            v => v.ListDocumentsAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Spec §6.4.5 / §14.4a's first decision, and the regression guard for it: showing a project that is
    /// not loaded in this session must still render its documents, and must never call <c>LoadProject</c>
    /// — inspection is a pure read with no side effects.
    /// </summary>
    [Fact]
    public async Task ShowCommand_UnloadedProject_StillRendersDocuments_AndDoesNotLoadIt()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1", null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentInfo> { new("alpha.md", "*", "x") });

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show x", session));

        Assert.Contains("alpha.md", output);
        stateMock.Verify(s => s.LoadProject(It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// Spec §6.4.5 / §14.4a's decision: <c>/project-show</c> is the only project command with zero side
    /// effects — it must never call <see cref="DocumentContextHydrationService.MarkDirty"/>. Uses the same
    /// <see cref="GetCleanHydrationAsync"/> spy technique Task 23's <c>CreateAsync</c> tests established.
    /// </summary>
    [Fact]
    public async Task ShowCommand_DoesNotMarkHydrationDirty()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.SessionId).Returns("s1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);
        DocumentContextHydrationService hydration = await GetCleanHydrationAsync(session, vectorStoreMock);

        await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show x", session));

        Assert.False(hydration.IsDirty);
    }

    /// <summary>
    /// Spec §8.1 Step 3: <c>/project-show HandBook</c> resolves to and renders the stored <c>"handbook"</c>
    /// project — exercising <see cref="ProjectsCommand.Resolve"/> from inside the full command.
    /// </summary>
    [Fact]
    public async Task ShowCommand_MixedCaseInput_ResolvesToStoredId()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "handbook" });
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1", null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentInfo> { new("alpha.md", "*", "handbook") });

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(
            () => ProjectsCommand.ShowAsync("/project-show HandBook", session));

        vectorStoreMock.Verify(
            v => v.ListDocumentsAsync(
                "u1",
                null,
                It.Is<IReadOnlyList<string>>(p => p.Count == 1 && p[0] == "handbook"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Contains("in project 'handbook'", output);
    }

    /// <summary>
    /// Spec §6.4.5 flow / §6.4.6 (6.4.22): no argument and no known projects prints "No projects found." —
    /// identical wording to <c>/project-delete</c>'s picker guard.
    /// </summary>
    [Fact]
    public async Task ShowCommand_NoArgument_WithNoProjects_PrintsNoProjectsFound()
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns([]);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show", session));

        Assert.Equal("No projects found.", output);
    }

    /// <summary>
    /// Spec §6.4.5 output format: the footer line reports the project's loaded status in this session —
    /// "● loaded in this session" or "○ not loaded in this session" — based on
    /// <c>state.LoadedProjects.Contains(id, OrdinalIgnoreCase)</c>, independent of the row content.
    /// </summary>
    [Theory]
    [InlineData(true, "● loaded in this session")]
    [InlineData(false, "○ not loaded in this session")]
    public async Task ShowCommand_FooterReportsLoadedState(bool isLoaded, string expectedSuffix)
    {
        var stateMock = new Mock<IProjectSessionState>();
        stateMock.SetupGet(s => s.UserId).Returns("u1");
        stateMock.SetupGet(s => s.LoadedProjects).Returns(isLoaded ? ["x"] : []);

        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(v => v.ListProjectsAsync("u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "x" });
        vectorStoreMock
            .Setup(v => v.ListDocumentsAsync(
                "u1", null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentInfo> { new("alpha.md", "*", "x") });

        ConsoleChatSession session = CreateSession(stateMock.Object, vectorStoreMock);

        string output = await RunCapturingOutputAsync(() => ProjectsCommand.ShowAsync("/project-show x", session));

        Assert.EndsWith(expectedSuffix, output);
    }
}
