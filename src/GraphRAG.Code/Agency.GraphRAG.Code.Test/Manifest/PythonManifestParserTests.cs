using Agency.GraphRAG.Code.Manifest;

namespace Agency.GraphRAG.Code.Test.Manifest;

/// <summary>
/// Tests for <see cref="PythonManifestParser"/>.
/// </summary>
public sealed class PythonManifestParserTests
{
    [Fact]
    public void Parse_PyProject_SupportsPoetryDependenciesAndPathReferences()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        string manifestPath = workspace.WriteFile(
            "services/api/pyproject.toml",
            """
            [tool.poetry]
            name = "api-service"

            [tool.poetry.dependencies]
            python = "^3.12"
            fastapi = "^0.115.0"
            shared-lib = { path = "../shared-lib" }

            [tool.poetry.group.dev.dependencies]
            pytest = "^8.3.0"
            """);
        workspace.WriteFile(
            "services/shared-lib/pyproject.toml",
            """
            [tool.poetry]
            name = "shared-lib"
            """);

        PythonManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        Assert.Equal("api-service", result.ProjectName);
        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "fastapi" && dependency.Version == "^0.115.0" && dependency.Scope == "runtime");
        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "pytest" && dependency.Version == "^8.3.0" && dependency.Scope == "dev");
        ManifestProjectReference projectReference = Assert.Single(result.ProjectReferences);
        Assert.Equal("shared-lib", projectReference.Name);
        Assert.Equal("services/shared-lib/pyproject.toml", projectReference.ManifestRelativePath);
    }

    [Fact]
    public void Parse_PyProject_SupportsUvDependenciesAndPathSources()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        string manifestPath = workspace.WriteFile(
            "packages/app/pyproject.toml",
            """
            [project]
            name = "uv-app"
            dependencies = [
              "requests>=2.32.0",
              "shared-lib"
            ]

            [dependency-groups]
            dev = ["pytest>=8.3.0"]

            [tool.uv.sources]
            shared-lib = { path = "../shared-lib" }
            """);
        workspace.WriteFile(
            "packages/shared-lib/pyproject.toml",
            """
            [project]
            name = "shared-lib"
            """);

        PythonManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "requests" && dependency.Version == ">=2.32.0" && dependency.Scope == "runtime");
        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "pytest" && dependency.Version == ">=8.3.0" && dependency.Scope == "dev");
        ManifestProjectReference projectReference = Assert.Single(result.ProjectReferences);
        Assert.Equal("shared-lib", projectReference.Name);
        Assert.Equal("packages/shared-lib/pyproject.toml", projectReference.ManifestRelativePath);
    }

    [Fact]
    public void Parse_RequirementsTxt_IsUsedAsFallback()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        string manifestPath = workspace.WriteFile(
            "scripts/requirements.txt",
            """
            requests==2.32.3
            # comment
            numpy>=2.0
            """);

        PythonManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        Assert.Equal("scripts", result.ProjectName);
        Assert.Equal(2, result.ExternalDependencies.Count);
        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "requests" && dependency.Version == "==2.32.3");
        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "numpy" && dependency.Version == ">=2.0");
        Assert.Empty(result.ProjectReferences);
    }

    [Theory]
    [InlineData("setup.py")]
    [InlineData("Pipfile")]
    [InlineData("environment.yml")]
    public void CanParse_IgnoresUnsupportedPythonManifestFiles(string fileName)
    {
        PythonManifestParser parser = new();

        bool canParse = parser.CanParse(fileName);

        Assert.False(canParse);
    }
}
