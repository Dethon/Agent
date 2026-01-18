namespace Agent.Settings;

public record CommandLineParams
{
    public ChatInterface ChatInterface { get; init; } = ChatInterface.Web;
    public string? Prompt { get; init; }
    public bool ShowReasoning { get; init; }
}

public enum ChatInterface
{
    Cli,
    Telegram,
    OneShot,
    Web
}