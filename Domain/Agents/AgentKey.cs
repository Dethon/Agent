namespace Domain.Agents;

public readonly record struct AgentKey(string ConversationId, string? AgentId = null)
{
    public override string ToString()
    {
        return $"agent-key:{AgentId}:{ConversationId}";
    }
}