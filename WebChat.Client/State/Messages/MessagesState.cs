using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public sealed record MessagesState
{
    public IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> MessagesByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ChatMessageModel>>();

    public IReadOnlySet<string> LoadedTopics { get; init; }
        = new HashSet<string>();

    /// <summary>
    ///     Tracks message IDs that have been finalized (added from streaming).
    ///     Used to prevent duplicate messages when both HandleUserMessage and
    ///     StreamingService try to add the same message due to race conditions.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> FinalizedMessageIdsByTopic { get; init; }
        = new Dictionary<string, IReadOnlySet<string>>();

    public static MessagesState Initial => new();
}