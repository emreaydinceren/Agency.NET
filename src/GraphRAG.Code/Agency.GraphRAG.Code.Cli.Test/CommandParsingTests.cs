namespace Agency.GraphRAG.Code.Cli.Test;

/// <summary>
/// Verifies CLI application is built correctly using Spectre.Console.
/// </summary>
public sealed class CommandParsingTests
{
    [Fact]
    public void BuildApplication_CreatesApplicationSuccessfully()
    {
        var app = CliApplication.BuildApplication(@"E:\Repos\Agency");
        Assert.NotNull(app);
    }

    [Fact]
    public void BuildApplication_WithDifferentWorkingDirectory()
    {
        var app = CliApplication.BuildApplication("/different/path");
        Assert.NotNull(app);
    }

    [Fact]
    public void BuildApplication_ThrowsOnNullWorkingDirectory()
    {
        Assert.Throws<ArgumentNullException>(() => CliApplication.BuildApplication(null!));
    }

    [Fact]
    public void BuildApplication_ThrowsOnEmptyWorkingDirectory()
    {
        Assert.Throws<ArgumentException>(() => CliApplication.BuildApplication(""));
    }
}
