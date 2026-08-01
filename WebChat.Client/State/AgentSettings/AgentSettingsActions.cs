namespace WebChat.Client.State.AgentSettings;

public record SetAgentModel(string AgentId, string? Model) : IAction;

public record SetAgentReasoningEffort(string AgentId, string? Effort) : IAction;

public record AgentSettingsLoaded(string AgentId, AgentModelSettings Settings) : IAction;