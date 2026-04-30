using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Test.Domain;

/// <summary>
/// Tests for entity record types: Repo, Project, SourceFile, Module, ExternalPackage.
/// </summary>
public sealed class EntityRecordsTests
{
    // ─── Repo ───────────────────────────────────────────────────────────────

    [Fact]
    public void Repo_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var indexedAt = DateTimeOffset.UtcNow;

        var repo = new Repo
        {
            Id = id,
            RemoteUrl = "https://github.com/example/repo",
            LocalPath = "/repos/example",
            IsShallow = true,
            IndexedCommit = "abc123",
            IndexedAt = indexedAt,
        };

        Assert.Equal(id, repo.Id);
        Assert.Equal("https://github.com/example/repo", repo.RemoteUrl);
        Assert.Equal("/repos/example", repo.LocalPath);
        Assert.True(repo.IsShallow);
        Assert.Equal("abc123", repo.IndexedCommit);
        Assert.Equal(indexedAt, repo.IndexedAt);
    }

    [Fact]
    public void Repo_WithExpression_ProducesNewRecord()
    {
        var original = new Repo
        {
            Id = Guid.NewGuid(),
            LocalPath = "/original",
            IsShallow = false,
        };

        var mutated = original with { LocalPath = "/mutated", IsShallow = true };

        Assert.Equal("/mutated", mutated.LocalPath);
        Assert.True(mutated.IsShallow);
        Assert.Equal(original.Id, mutated.Id);
        Assert.NotSame(original, mutated);
    }

    [Fact]
    public void Repo_SameValues_AreEqual()
    {
        var id = Guid.NewGuid();

        var a = new Repo { Id = id, LocalPath = "/path", IsShallow = false };
        var b = new Repo { Id = id, LocalPath = "/path", IsShallow = false };

        Assert.Equal(a, b);
    }

    [Fact]
    public void Repo_NullableProperties_AcceptNull()
    {
        var repo = new Repo
        {
            Id = Guid.NewGuid(),
            RemoteUrl = null,
            LocalPath = "/path",
            IsShallow = false,
            IndexedCommit = null,
            IndexedAt = null,
        };

        Assert.Null(repo.RemoteUrl);
        Assert.Null(repo.IndexedCommit);
        Assert.Null(repo.IndexedAt);
    }

    // ─── Project ─────────────────────────────────────────────────────────────

    [Fact]
    public void Project_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var project = new Project
        {
            Id = id,
            RepoId = repoId,
            Language = "csharp",
            Name = "MyProject",
            RelativePath = "src/MyProject",
            ManifestPath = "src/MyProject/MyProject.csproj",
        };

        Assert.Equal(id, project.Id);
        Assert.Equal(repoId, project.RepoId);
        Assert.Equal("csharp", project.Language);
        Assert.Equal("MyProject", project.Name);
        Assert.Equal("src/MyProject", project.RelativePath);
        Assert.Equal("src/MyProject/MyProject.csproj", project.ManifestPath);
    }

    [Fact]
    public void Project_WithExpression_MutatesLanguage()
    {
        var original = new Project
        {
            Id = Guid.NewGuid(),
            RepoId = Guid.NewGuid(),
            Language = "csharp",
            Name = "Proj",
            RelativePath = "src/Proj",
        };

        var mutated = original with { Language = "fsharp" };

        Assert.Equal("fsharp", mutated.Language);
        Assert.Equal(original.Id, mutated.Id);
    }

    [Fact]
    public void Project_SameValues_AreEqual()
    {
        var id = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var a = new Project { Id = id, RepoId = repoId, Language = "csharp", Name = "P", RelativePath = "src/P" };
        var b = new Project { Id = id, RepoId = repoId, Language = "csharp", Name = "P", RelativePath = "src/P" };

        Assert.Equal(a, b);
    }

    [Fact]
    public void Project_NullManifestPath_IsValid()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            RepoId = Guid.NewGuid(),
            Language = "typescript",
            Name = "Proj",
            RelativePath = "src/Proj",
            ManifestPath = null,
        };

        Assert.Null(project.ManifestPath);
    }

    // ─── SourceFile ───────────────────────────────────────────────────────────

    [Fact]
    public void SourceFile_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var file = new SourceFile
        {
            Id = id,
            ProjectId = projectId,
            RepoId = repoId,
            Path = "src/Foo.cs",
            Language = "csharp",
            ContentHash = "deadbeef",
        };

        Assert.Equal(id, file.Id);
        Assert.Equal(projectId, file.ProjectId);
        Assert.Equal(repoId, file.RepoId);
        Assert.Equal("src/Foo.cs", file.Path);
        Assert.Equal("csharp", file.Language);
        Assert.Equal("deadbeef", file.ContentHash);
    }

    [Fact]
    public void SourceFile_WithExpression_MutatesPath()
    {
        var original = new SourceFile
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            RepoId = Guid.NewGuid(),
            Path = "src/Original.cs",
            Language = "csharp",
        };

        var mutated = original with { Path = "src/Mutated.cs" };

        Assert.Equal("src/Mutated.cs", mutated.Path);
        Assert.Equal(original.Id, mutated.Id);
    }

    [Fact]
    public void SourceFile_SameValues_AreEqual()
    {
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        var a = new SourceFile { Id = id, ProjectId = projectId, RepoId = repoId, Path = "Foo.cs", Language = "csharp" };
        var b = new SourceFile { Id = id, ProjectId = projectId, RepoId = repoId, Path = "Foo.cs", Language = "csharp" };

        Assert.Equal(a, b);
    }

    [Fact]
    public void SourceFile_NullContentHash_IsValid()
    {
        var file = new SourceFile
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            RepoId = Guid.NewGuid(),
            Path = "Foo.cs",
            Language = "csharp",
            ContentHash = null,
        };

        Assert.Null(file.ContentHash);
    }

    // ─── Module ───────────────────────────────────────────────────────────────

    [Fact]
    public void Module_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var module = new Module
        {
            Id = id,
            FileId = fileId,
            Name = "MyNamespace",
            Kind = "namespace",
        };

        Assert.Equal(id, module.Id);
        Assert.Equal(fileId, module.FileId);
        Assert.Equal("MyNamespace", module.Name);
        Assert.Equal("namespace", module.Kind);
    }

    [Fact]
    public void Module_WithExpression_MutatesKind()
    {
        var original = new Module
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Name = "Mod",
            Kind = "namespace",
        };

        var mutated = original with { Kind = "class" };

        Assert.Equal("class", mutated.Kind);
        Assert.Equal(original.Id, mutated.Id);
    }

    [Fact]
    public void Module_SameValues_AreEqual()
    {
        var id = Guid.NewGuid();
        var fileId = Guid.NewGuid();

        var a = new Module { Id = id, FileId = fileId, Name = "M", Kind = "namespace" };
        var b = new Module { Id = id, FileId = fileId, Name = "M", Kind = "namespace" };

        Assert.Equal(a, b);
    }

    // ─── ExternalPackage ─────────────────────────────────────────────────────

    [Fact]
    public void ExternalPackage_ConstructionWithAllProperties_SetsCorrectly()
    {
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var pkg = new ExternalPackage
        {
            Id = id,
            ProjectId = projectId,
            Name = "Newtonsoft.Json",
            Version = "13.0.3",
            Ecosystem = "nuget",
            Scope = "runtime",
        };

        Assert.Equal(id, pkg.Id);
        Assert.Equal(projectId, pkg.ProjectId);
        Assert.Equal("Newtonsoft.Json", pkg.Name);
        Assert.Equal("13.0.3", pkg.Version);
        Assert.Equal("nuget", pkg.Ecosystem);
        Assert.Equal("runtime", pkg.Scope);
    }

    [Fact]
    public void ExternalPackage_WithExpression_MutatesVersion()
    {
        var original = new ExternalPackage
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "SomePkg",
            Ecosystem = "npm",
            Scope = "dev",
        };

        var mutated = original with { Version = "2.0.0" };

        Assert.Equal("2.0.0", mutated.Version);
        Assert.Equal(original.Id, mutated.Id);
    }

    [Fact]
    public void ExternalPackage_SameValues_AreEqual()
    {
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var a = new ExternalPackage { Id = id, ProjectId = projectId, Name = "Pkg", Ecosystem = "nuget", Scope = "runtime" };
        var b = new ExternalPackage { Id = id, ProjectId = projectId, Name = "Pkg", Ecosystem = "nuget", Scope = "runtime" };

        Assert.Equal(a, b);
    }

    [Fact]
    public void ExternalPackage_NullVersion_IsValid()
    {
        var pkg = new ExternalPackage
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "Pkg",
            Version = null,
            Ecosystem = "pypi",
            Scope = "peer",
        };

        Assert.Null(pkg.Version);
    }
}
