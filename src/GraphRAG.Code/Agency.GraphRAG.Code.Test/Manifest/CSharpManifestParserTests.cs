using Agency.GraphRAG.Code.Manifest;

namespace Agency.GraphRAG.Code.Test.Manifest;

/// <summary>
/// Tests for <see cref="CSharpManifestParser"/>.
/// </summary>
public sealed class CSharpManifestParserTests
{
    [Fact]
    public void Parse_ResolvesCentralPackageVersionsAcrossDirectoryChainAndProjectReferences()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        workspace.WriteFile(
            "Directory.Packages.props",
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """);
        workspace.WriteFile(
            "src/Directory.Packages.props",
            """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Serilog" Version="4.1.0" />
                <PackageVersion Include="Moq" Version="4.20.72" />
              </ItemGroup>
            </Project>
            """);
        string manifestPath = workspace.WriteFile(
            "src/App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <AssemblyName>Contoso.App</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageReference Include="Moq" />
                <ProjectReference Include="..\Shared\Shared.csproj" />
              </ItemGroup>
            </Project>
            """);
        workspace.WriteFile(
            "src/Shared/Shared.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        CSharpManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        Assert.Equal("Contoso.App", result.ProjectName);
        Assert.Equal("src/App/App.csproj", result.ManifestRelativePath);
        Assert.Equal("src/App", result.ProjectRelativePath);
        Assert.Equal("nuget", result.Ecosystem);
        Assert.Collection(
            result.ExternalDependencies.OrderBy(dependency => dependency.Name),
            dependency =>
            {
                Assert.Equal("Moq", dependency.Name);
                Assert.Equal("4.20.72", dependency.Version);
                Assert.Equal("runtime", dependency.Scope);
            },
            dependency =>
            {
                Assert.Equal("Newtonsoft.Json", dependency.Name);
                Assert.Equal("13.0.3", dependency.Version);
                Assert.Equal("runtime", dependency.Scope);
            },
            dependency =>
            {
                Assert.Equal("Serilog", dependency.Name);
                Assert.Equal("4.1.0", dependency.Version);
                Assert.Equal("runtime", dependency.Scope);
            });
        ManifestProjectReference projectReference = Assert.Single(result.ProjectReferences);
        Assert.Equal("Shared", projectReference.Name);
        Assert.Equal("src/Shared/Shared.csproj", projectReference.ManifestRelativePath);
    }
}
