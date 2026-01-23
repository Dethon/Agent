using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public sealed record MessagesState
{
    public IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> MessagesByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ChatMessageModel>>();

    public IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> PendingMessagesByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ChatMessageModel>>();

    public IReadOnlySet<string> LoadedTopics { get; init; }
        = new HashSet<string>();


    public static MessagesState Initial => new();
}