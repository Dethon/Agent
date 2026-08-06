using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public sealed record MessagesState
{
    public IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> MessagesByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ChatMessageModel>>();

    public IReadOnlySet<string> LoadedTopics { get; init; }
        = new HashSet<string>();

    // Used to prevent duplicate messages when both HandleUserMessage and
    // StreamingService try to add the same message due to race conditions.
    public IReadOnlyDictionary<string, IReadOnlySet<string>> FinalizedMessageIdsByTopic { get; init; }
        = new Dictionary<string, IReadOnlySet<string>>();

    // A message id the topic already has a committed bubble for. Both the chunk loop and the
    // module ask this, so the walk over the set lives here rather than in each of them.
    public bool IsFinalized(string topicId, string? messageId) =>
        messageId is not null &&
        FinalizedMessageIdsByTopic.GetValueOrDefault(topicId)?.Contains(messageId) == true;

    public static MessagesState Initial => new();
}