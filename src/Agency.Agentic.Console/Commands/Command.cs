using Microsoft.Extensions.Hosting;
using static System.Collections.Specialized.BitVector32;

namespace Agency.Agentic.Console.Commands;

internal class Command (string Name, string Description)
{
    public string CommandText { get; } = Name;

    public string Description { get; } = Description;

    public required Func<string, ConsoleChatSession, Task<CommandContinuation>> Execute { get; set; }
}