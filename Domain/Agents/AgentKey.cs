namespace Domain.Agents;

public readonly record struct AgentKey(long ChatId, long ThreadId, string? AgentId = null)
{
    public override string ToString()
    {
        return $"agent-key:{AgentId}:{ChatId}:{ThreadId}";
    }
}