using System.Net;
using System.Text;

namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="Doctor"/> and <see cref="EndpointProbe"/>, using a stubbed embeddings endpoint.</summary>
public sealed class DoctorTests
{
    private static readonly CliArguments NoOptions = CliArguments.Parse(["doctor"]);

    /// <summary>Verifies a missing embedding configuration is reported with a fix, not thrown as a usage error.</summary>
    [Fact]
    public async Task RunAsync_NoEmbeddingConfigured_ReportsFailingCheckWithFix()
    {
        using var dir = new TempDirectory();

        IReadOnlyList<DoctorCheck> checks = await RunAsync(dir, new StubEndpoint());

        DoctorCheck embedding = Assert.Single(checks, c => c.Name == "embedding_config");
        Assert.False(embedding.Ok);
        Assert.Contains("indexer.json", embedding.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(checks, c => c.Name == "endpoint");
        Assert.False(Assert.Single(checks, c => c.Name == "skill").Ok);
    }

    /// <summary>Verifies a configured, reachable endpoint with matching dimensions passes every check.</summary>
    [Fact]
    public async Task RunAsync_ConfiguredEndpointMatchingDimensions_AllChecksPass()
    {
        using var dir = new TempDirectory();
        WriteConfig(dir, dimensions: 3);
        await SkillInstaller.InstallAsync([Path.Combine(dir.Path, "work", ".claude", "skills")], TestContext.Current.CancellationToken);

        IReadOnlyList<DoctorCheck> checks = await RunAsync(dir, new StubEndpoint { Models = ["m1", "other"], VectorLength = 3 });

        Assert.All(checks, c => Assert.True(c.Ok, $"{c.Name}: {c.Detail}"));
        Assert.Contains(checks, c => c.Name == "dimensions");
        Assert.Contains("current", Assert.Single(checks, c => c.Name == "skill").Detail, StringComparison.Ordinal);
    }

    /// <summary>Verifies a dimension mismatch is measured from the endpoint and reported with the right value.</summary>
    [Fact]
    public async Task RunAsync_WrongConfiguredDimensions_ReportsMeasuredValue()
    {
        using var dir = new TempDirectory();
        WriteConfig(dir, dimensions: 1024);

        IReadOnlyList<DoctorCheck> checks = await RunAsync(dir, new StubEndpoint { Models = ["m1"], VectorLength = 768 });

        DoctorCheck dimensions = Assert.Single(checks, c => c.Name == "dimensions");
        Assert.False(dimensions.Ok);
        Assert.Contains("Embedding:Dimensions to 768", dimensions.Fix, StringComparison.Ordinal);
        Assert.True(Assert.Single(checks, c => c.Name == "model").Ok);
    }

    /// <summary>Verifies a model the server does not list is reported as missing.</summary>
    [Fact]
    public async Task RunAsync_ModelNotListed_ReportsModelCheckFailure()
    {
        using var dir = new TempDirectory();
        WriteConfig(dir, dimensions: 3);

        IReadOnlyList<DoctorCheck> checks = await RunAsync(dir, new StubEndpoint { Models = ["another-model"], VectorLength = 3 });

        Assert.False(Assert.Single(checks, c => c.Name == "model").Ok);
    }

    /// <summary>Verifies an unreachable endpoint is reported, and the remaining checks still run.</summary>
    [Fact]
    public async Task RunAsync_EndpointDown_ReportsEndpointFailureAndContinues()
    {
        using var dir = new TempDirectory();
        WriteConfig(dir, dimensions: 3);

        IReadOnlyList<DoctorCheck> checks = await RunAsync(dir, new StubEndpoint { Down = true });

        Assert.False(Assert.Single(checks, c => c.Name == "endpoint").Ok);
        Assert.DoesNotContain(checks, c => c.Name == "dimensions");
        Assert.True(Assert.Single(checks, c => c.Name == "database").Ok);
    }

    /// <summary>Verifies a skill that differs from the bundled one is reported as stale.</summary>
    [Fact]
    public async Task RunAsync_SkillDiffersFromBundled_ReportsStale()
    {
        using var dir = new TempDirectory();
        string root = Path.Combine(dir.Path, "work", ".claude", "skills");
        IReadOnlyList<InstalledSkill> written = await SkillInstaller.InstallAsync([root], TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(written[0].Path,"\nlocal edit", TestContext.Current.CancellationToken);

        IReadOnlyList<DoctorCheck> checks = await RunAsync(dir, new StubEndpoint());

        DoctorCheck skill = Assert.Single(checks, c => c.Name == "skill");
        Assert.False(skill.Ok);
        Assert.Contains("stale", skill.Detail, StringComparison.Ordinal);
    }

    /// <summary>Verifies the probe reads the vector length from the first embedding returned.</summary>
    [Fact]
    public async Task MeasureDimensionsAsync_ReturnsVectorLength()
    {
        using var http = new HttpClient(new StubEndpoint { VectorLength = 5 });
        var options = new Agency.Embeddings.OpenAI.EmbeddingOptions { BaseUrl = "http://stub/v1/", ModelId = "m1", ApiKey = "k" };

        int length = await EndpointProbe.MeasureDimensionsAsync(http, options, TestContext.Current.CancellationToken);

        Assert.Equal(5, length);
    }

    private static Task<IReadOnlyList<DoctorCheck>> RunAsync(TempDirectory dir, StubEndpoint endpoint)
    {
        string home = Path.Combine(dir.Path, "home");
        string work = Path.Combine(dir.Path, "work");
        Directory.CreateDirectory(work);
        var http = new HttpClient(endpoint);
        return Doctor.RunAsync(NoOptions, home, Path.Combine(dir.Path, "profile"), work, http, TestContext.Current.CancellationToken);
    }

    private static void WriteConfig(TempDirectory dir, int dimensions)
    {
        string home = Path.Combine(dir.Path, "home");
        Directory.CreateDirectory(home);
        File.WriteAllText(
            Path.Combine(home, "indexer.json"),
            $$"""{ "Embedding": { "BaseUrl": "http://stub/v1", "ModelId": "m1", "Dimensions": {{dimensions}} } }""");
    }

    /// <summary>Answers <c>/models</c> and <c>/embeddings</c> like an OpenAI-compatible server.</summary>
    internal sealed class StubEndpoint : HttpMessageHandler
    {
        public IReadOnlyList<string> Models { get; init; } = [];

        public int VectorLength { get; init; } = 3;

        public bool Down { get; init; }

        /// <summary>Gets the <c>Authorization</c> header of the last request, or <see langword="null"/> if it had none.</summary>
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastAuthorization = request.Headers.Authorization?.ToString();
            if (this.Down)
            {
                throw new HttpRequestException("connection refused");
            }

            string json = request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? $$"""{ "data": [{{string.Join(",", this.Models.Select(m => $$"""{ "id": "{{m}}" }"""))}}] }"""
                : $$"""{ "data": [{ "embedding": [{{string.Join(",", Enumerable.Repeat("0.5", this.VectorLength))}}] }] }""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
