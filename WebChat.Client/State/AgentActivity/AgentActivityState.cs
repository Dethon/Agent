using System.Collections.Immutable;

namespace WebChat.Client.State.AgentActivity;

public sealed record AgentActivityState
{
    public ImmutableDictionary<string, string> TopicToAgent { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    public ImmutableHashSet<string> AgentsWithUnseenActivity { get; init; } = [];

    public static AgentActivityState Initial => new();
}