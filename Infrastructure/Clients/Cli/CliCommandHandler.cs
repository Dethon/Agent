namespace Infrastructure.Clients.Cli;

internal sealed class CliCommandHandler(
    Action clearHistory,
    Action<string, string, bool, bool, bool> addToHistory)
{
    private static readonly string[] _helpLines =
    [
        "Available commands:",
        "  /help, /?     - Show this help",
        "  /clear, /cls  - Clear conversation and start fresh",
        "  Ctrl+C twice  - Exit application"
    ];

    public bool TryHandleCommand(string input)
    {
        switch (input.ToLowerInvariant())
        {
            case "/clear":
            case "/cls":
                clearHistory();
                return true;

            case "/help":
            case "/?":
                foreach (var line in _helpLines)
                {
                    addToHistory("[Help]", line, false, false, true);
                }

                return true;

            default:
                return false;
        }
    }
}