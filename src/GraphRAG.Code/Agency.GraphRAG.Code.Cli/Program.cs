namespace Agency.GraphRAG.Code.Cli;

using Serilog;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        // Encoding and Log initialization remain at the very top
        System.Console.OutputEncoding = System.Text.Encoding.UTF8;
        System.Console.InputEncoding = System.Text.Encoding.UTF8;
        int result = CliApplication.BuildApplication(Directory.GetCurrentDirectory()).Run(args);
        await Log.CloseAndFlushAsync();
        return result;
    }
}
