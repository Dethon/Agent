namespace Infrastructure.Clients.Cli;

internal sealed class CliCommandHandler(ITerminalAdapter terminalAdapter, Action<bool> onReset)
{
    private const string HelpText = """
                                    Available commands:
                                      /help, /?     - Show this help
                                      /cancel       - Cancel current operation (keeps conversation history)
                                      /clear        - Clear conversation and wipe thread history
                                      Ctrl+C twice  - Exit application
                                    """;

    public bool TryHandleCommand(string input)
    {
        switch (input.ToLowerInvariant())
        {
            case "/cancel":
                onReset(false);
                return true;

            case "/clear":
                onReset(true);
                terminalAdapter.ClearDisplay();
                return true;

            case "/help":
            case "/?":
                terminalAdapter.ShowSystemMessage(HelpText);
                return true;

            default:
                return false;
        }
    }
}