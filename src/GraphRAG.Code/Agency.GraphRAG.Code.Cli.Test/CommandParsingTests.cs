using System.CommandLine;

namespace Agency.GraphRAG.Code.Cli.Test;

/// <summary>
/// Verifies CLI command parsing for the V1 command surface.
/// </summary>
public sealed class CommandParsingTests
{
    [Fact]
    public void RootCommand_ParsesIndexCommandShape()
    {
        RootCommand rootCommand = CliApplication.BuildRootCommand(@"E:\Repos\Agency");

        var parseResult = rootCommand.Parse("index E:\\Repos\\Agency --store sqlite --connection \"Data Source=code.db\"");
        string[] tokenValues = parseResult.Tokens.Select(static token => token.Value).ToArray();

        Assert.Equal("index", parseResult.CommandResult.Command.Name);
        Assert.Contains(@"E:\Repos\Agency", tokenValues);
        Assert.Contains("sqlite", tokenValues);
        Assert.Contains("code.db", string.Join(" ", tokenValues), StringComparison.Ordinal);
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void RootCommand_ParsesQueryCommandShape()
    {
        RootCommand rootCommand = CliApplication.BuildRootCommand(@"E:\Repos\Agency");

        var parseResult = rootCommand.Parse("query \"where is Agent defined\" --store postgres --connection Host=db --top-k 7");
        string[] tokenValues = parseResult.Tokens.Select(static token => token.Value).ToArray();

        Assert.Equal("query", parseResult.CommandResult.Command.Name);
        Assert.Contains("where is Agent defined", tokenValues);
        Assert.Contains("postgres", tokenValues);
        Assert.Contains("Host=db", tokenValues);
        Assert.Contains("7", tokenValues);
        Assert.Empty(parseResult.Errors);
    }
}
