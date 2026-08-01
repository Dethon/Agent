namespace WebChat.Client.State.AgentSettings;

public sealed record AgentModelSettings(string? Model, string? ReasoningEffort);

public sealed record AgentSettingsState
{
    public IReadOnlyDictionary<string, AgentModelSettings> ByAgent { get; init; } =
        new Dictionary<string, AgentModelSettings>();

    public static AgentSettingsState Initial => new();
}