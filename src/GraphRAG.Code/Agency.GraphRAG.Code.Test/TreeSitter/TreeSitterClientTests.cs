using System.Diagnostics;
using Agency.GraphRAG.Code.TreeSitter;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.TreeSitter;

/// <summary>
/// Tests for <see cref="TreeSitterClient"/>.
/// </summary>
public sealed class TreeSitterClientTests
{
    public static TheoryData<Language, string, string, string, int> ParseCases =>
        new()
        {
            { Language.CSharp, "Sample.cs", "class Worker { void Run() { } }", "compilation_unit", 4 },
            { Language.TypeScript, "sample.ts", "export function run(value: string) { return value; }", "program", 4 },
            { Language.JavaScript, "sample.js", "function run(value) { return value; }", "program", 4 },
            { Language.Python, "sample.py", "def run(value):\n    return value\n", "module", 3 },
        };

    [Theory]
    [MemberData(nameof(ParseCases))]
    public async Task ParseAsync_ReturnsAstForSupportedLanguage(Language language, string path, string source, string expectedRootKind, int minimumNodeCount)
    {
        SkipIfTreeSitterUnavailable();

        await using var client = new TreeSitterClient();

        ParsedFile parsed = await client.ParseAsync(path, language, source, TestContext.Current.CancellationToken);

        Assert.Equal(expectedRootKind, parsed.Root.Kind);
        Assert.True(CountNodes(parsed.Root) >= minimumNodeCount);
    }

    private static void SkipIfTreeSitterUnavailable()
    {
        if (!CanRun("node", "--version"))
        {
            Assert.Skip("node is not available on PATH.");
        }

        string sidecarPath = Path.Combine("E:\\Repos\\Agency", "tools", "treesitter-sidecar", "index.js");
        if (!File.Exists(sidecarPath))
        {
            Assert.Skip("Tree-sitter sidecar is not available yet.");
        }

        string[] requiredPackages =
        [
            "tree-sitter",
            "tree-sitter-c-sharp",
            "tree-sitter-javascript",
            "tree-sitter-python",
            "tree-sitter-typescript",
        ];

        string nodeModulesPath = Path.Combine("E:\\Repos\\Agency", "tools", "treesitter-sidecar", "node_modules");
        if (requiredPackages.Any(package => !Directory.Exists(Path.Combine(nodeModulesPath, package))))
        {
            Assert.Skip("Tree-sitter sidecar dependencies are not installed yet.");
        }
    }

    private static bool CanRun(string fileName, string arguments)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Failed to start process.");

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int CountNodes(AstNode node)
    {
        return 1 + node.Children.Sum(CountNodes);
    }
}
