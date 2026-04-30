using System.CommandLine;

namespace Agency.GraphRAG.Code.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        return CliApplication.BuildRootCommand(Directory.GetCurrentDirectory()).Invoke(args);
    }
}
