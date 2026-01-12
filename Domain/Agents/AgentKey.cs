namespace Domain.Agents;

public readonly record struct AgentKey(long ChatId, long ThreadId, string? BotTokenHash = null)
{
    public override string ToString()
    {
        return $"agent-key:{ChatId}:{ThreadId}";
    }
}