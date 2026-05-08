namespace Agency.GraphRAG.Code.Cli;

internal static class Program
{
    internal static int Main(string[] args)
    {
        // Encoding and Log initialization remain at the very top
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.InputEncoding = System.Text.Encoding.UTF8;
        return CliApplication.BuildApplication(Directory.GetCurrentDirectory()).Run(args);
    }
}
