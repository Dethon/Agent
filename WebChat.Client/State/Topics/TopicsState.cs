using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

/// <summary>
/// Immutable state for topic management: topic list, selection, and agents.
/// </summary>
public sealed record TopicsState
{
    public IReadOnlyList<StoredTopic> Topics { get; init; } = [];
    public string? SelectedTopicId { get; init; }
    public IReadOnlyList<AgentInfo> Agents { get; init; } = [];
    public string? SelectedAgentId { get; init; }
    public bool IsLoading { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Initial state with empty collections.
    /// </summary>
    public static TopicsState Initial => new();
}
