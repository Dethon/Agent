namespace Agent.Settings;

public record CommandLineParams
{
    public ChatInterface ChatInterface { get; init; } = ChatInterface.Telegram;
}

public enum ChatInterface
{
    Cli,
    Telegram
}