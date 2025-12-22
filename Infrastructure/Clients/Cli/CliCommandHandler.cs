namespace Infrastructure.Clients.Cli;

internal sealed class CliCommandHandler(ITerminalAdapter terminalAdapter, Action onClear)
{
    private const string HelpText = """
                                    Available commands:
                                      /help, /?     - Show this help
                                      /clear, /cls  - Clear conversation and start fresh
                                      Ctrl+C twice  - Exit application
                                    """;

    public bool TryHandleCommand(string input)
    {
        switch (input.ToLowerInvariant())
        {
            case "/clear":
            case "/cls":
                onClear();
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