namespace Agency.GraphRAG.Code.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        return CliApplication.BuildApplication(Directory.GetCurrentDirectory()).Run(args);
    }
}
