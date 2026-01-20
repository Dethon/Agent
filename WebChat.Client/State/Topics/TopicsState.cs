using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

public sealed record TopicsState
{
    public IReadOnlyList<StoredTopic> Topics { get; init; } = [];
    public string? SelectedTopicId { get; init; }
    public IReadOnlyList<AgentInfo> Agents { get; init; } = [];
    public string? SelectedAgentId { get; init; }
    public bool IsLoading { get; init; }
    public string? Error { get; init; }

    public static TopicsState Initial => new();
}