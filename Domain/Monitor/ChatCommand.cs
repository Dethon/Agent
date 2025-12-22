namespace Domain.Monitor;

public enum ChatCommand
{
    Cancel,
    Clear
}

public static class ChatCommandParser
{
    public static ChatCommand? Parse(string prompt)
    {
        return prompt.ToLowerInvariant() switch
        {
            "/clear" => ChatCommand.Clear,
            "/cancel" => ChatCommand.Cancel,
            _ => null
        };
    }
}