using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

/// <summary>
/// Immutable state for message storage per topic.
/// Messages are normalized by TopicId for O(1) topic switching.
/// </summary>
public sealed record MessagesState
{
    /// <summary>
    /// Messages indexed by TopicId. Each topic has its own list of messages.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> MessagesByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ChatMessageModel>>();

    /// <summary>
    /// Set of topic IDs that have been loaded from the server.
    /// Used to distinguish between "no messages" and "not yet loaded".
    /// </summary>
    public IReadOnlySet<string> LoadedTopics { get; init; }
        = new HashSet<string>();

    /// <summary>
    /// Initial state with empty collections.
    /// </summary>
    public static MessagesState Initial => new();
}
