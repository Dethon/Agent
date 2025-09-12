namespace Jack.Settings;

public record CommandLineParams
{
    public int WorkersCount { get; init; } = 10;
    public ChatInterface ChatInterface { get; init; } = ChatInterface.Telegram;
}

public enum ChatInterface
{
    Cli,
    Telegram
}