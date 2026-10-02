namespace Agency.Indexer.Test;

/// <summary>Tests for <see cref="CliArguments"/>.</summary>
public sealed class CliArgumentsTests
{
    /// <summary>Verifies options and flags are separated.</summary>
    [Fact]
    public void Parse_OptionsAndFlags()
    {
        CliArguments args = CliArguments.Parse(["index", "--index", "docs", "--wait", "--root", "./d"]);

        Assert.Equal("index", args.Command);
        Assert.Equal("docs", args.Require("index"));
        Assert.Equal("./d", args.Get("root"));
        Assert.Contains("wait", args.Flags);
    }

    /// <summary>Verifies that no arguments means help.</summary>
    [Fact]
    public void Parse_NoArguments_IsHelp()
    {
        Assert.Equal("help", CliArguments.Parse([]).Command);
    }

    /// <summary>Verifies malformed input is a usage error.</summary>
    [Theory]
    [InlineData("index", "--root")]
    [InlineData("index", "docs")]
    public void Parse_Malformed_Throws(string command, string argument)
    {
        Assert.Throws<UsageException>(() => CliArguments.Parse([command, argument]));
    }

    /// <summary>Verifies that a non-numeric integer option is a usage error.</summary>
    [Fact]
    public void GetPositiveInt_Invalid_Throws()
    {
        Assert.Throws<UsageException>(() => CliArguments.Parse(["search", "--top", "zero"]).GetPositiveInt("top", 5));
    }
}
