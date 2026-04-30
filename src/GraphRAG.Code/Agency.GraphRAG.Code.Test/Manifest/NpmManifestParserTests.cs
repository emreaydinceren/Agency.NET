using Agency.GraphRAG.Code.Manifest;

namespace Agency.GraphRAG.Code.Test.Manifest;

/// <summary>
/// Tests for <see cref="NpmManifestParser"/>.
/// </summary>
public sealed class NpmManifestParserTests
{
    [Fact]
    public void Parse_PrefersPnpmLockfileVersionsAndResolvesPnpmWorkspaceReferences()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        workspace.WriteFile(
            "pnpm-workspace.yaml",
            """
            packages:
              - "packages/*"
            """);
        workspace.WriteFile(
            "pnpm-lock.yaml",
            """
            lockfileVersion: '9.0'
            importers:
              packages/app:
                dependencies:
                  react:
                    specifier: ^18.2.0
                    version: 18.2.0
                  "@acme/shared":
                    specifier: workspace:^
                    version: link:../shared
            """);
        string manifestPath = workspace.WriteFile(
            "packages/app/package.json",
            """
            {
              "name": "@acme/app",
              "dependencies": {
                "react": "^18.2.0",
                "@acme/shared": "workspace:^"
              }
            }
            """);
        workspace.WriteFile(
            "packages/shared/package.json",
            """
            {
              "name": "@acme/shared",
              "version": "1.0.0"
            }
            """);

        NpmManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        Assert.Equal("@acme/app", result.ProjectName);
        ManifestPackageDependency react = Assert.Single(result.ExternalDependencies);
        Assert.Equal("react", react.Name);
        Assert.Equal("18.2.0", react.Version);
        ManifestProjectReference projectReference = Assert.Single(result.ProjectReferences);
        Assert.Equal("@acme/shared", projectReference.Name);
        Assert.Equal("packages/shared/package.json", projectReference.ManifestRelativePath);
    }

    [Fact]
    public void Parse_UsesPackageLockWhenPnpmLockIsAbsent()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        workspace.WriteFile(
            "package-lock.json",
            """
            {
              "name": "web-app",
              "lockfileVersion": 3,
              "packages": {
                "": {
                  "dependencies": {
                    "lodash": "^4.17.21"
                  }
                },
                "node_modules/lodash": {
                  "version": "4.17.21"
                }
              }
            }
            """);
        string manifestPath = workspace.WriteFile(
            "package.json",
            """
            {
              "name": "web-app",
              "dependencies": {
                "lodash": "^4.17.21"
              }
            }
            """);

        NpmManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        ManifestPackageDependency dependency = Assert.Single(result.ExternalDependencies);
        Assert.Equal("lodash", dependency.Name);
        Assert.Equal("4.17.21", dependency.Version);
    }

    [Fact]
    public void Parse_UsesYarnLockWhenHigherPriorityLockfilesAreAbsent()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        workspace.WriteFile(
            "yarn.lock",
            """
            react@^18.2.0:
              version "18.2.0"
            """);
        string manifestPath = workspace.WriteFile(
            "package.json",
            """
            {
              "name": "web-app",
              "dependencies": {
                "react": "^18.2.0"
              }
            }
            """);

        NpmManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        ManifestPackageDependency dependency = Assert.Single(result.ExternalDependencies);
        Assert.Equal("react", dependency.Name);
        Assert.Equal("18.2.0", dependency.Version);
    }

    [Fact]
    public void Parse_ResolvesNpmWorkspaceReferences()
    {
        using ManifestParserTestWorkspace workspace = ManifestParserTestWorkspace.Create();
        workspace.WriteFile(
            "package.json",
            """
            {
              "name": "repo-root",
              "private": true,
              "workspaces": [
                "packages/*"
              ]
            }
            """);
        string manifestPath = workspace.WriteFile(
            "packages/app/package.json",
            """
            {
              "name": "@acme/app",
              "dependencies": {
                "@acme/shared": "^1.0.0",
                "zod": "^3.25.0"
              },
              "devDependencies": {
                "vitest": "^2.0.0"
              }
            }
            """);
        workspace.WriteFile(
            "packages/shared/package.json",
            """
            {
              "name": "@acme/shared",
              "version": "1.0.0"
            }
            """);

        NpmManifestParser parser = new();

        ManifestParseResult result = parser.Parse(workspace.RootPath, manifestPath);

        Assert.Equal(2, result.ExternalDependencies.Count);
        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "zod" && dependency.Scope == "runtime");
        Assert.Contains(result.ExternalDependencies, dependency => dependency.Name == "vitest" && dependency.Scope == "dev");
        ManifestProjectReference projectReference = Assert.Single(result.ProjectReferences);
        Assert.Equal("packages/shared/package.json", projectReference.ManifestRelativePath);
    }
}
