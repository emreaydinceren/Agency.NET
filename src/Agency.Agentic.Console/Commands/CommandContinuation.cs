namespace Agency.Agentic.Console.Commands;

internal enum CommandContinuation
{
    Continue,   // Continue showing the command picker after executing the command.
    Clear,      // Clear the chat history and show the command picker again.
    ExitSession // Exit the entire chat session.
}
