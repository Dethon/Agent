namespace McpChannelVoice.Settings;

public record WyomingServerSettings
{
    public string Host { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 10700;
}