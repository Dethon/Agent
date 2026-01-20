using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

/// <summary>
/// Service for streaming AI responses to the client.
/// Dispatches store actions for state updates instead of using callbacks.
/// </summary>
public interface IStreamingService
{
    /// <summary>
    /// Streams a new response for the given message.
    /// </summary>
    /// <param name="topic">The topic to stream in</param>
    /// <param name="message">The user message to respond to</param>
    Task StreamResponseAsync(StoredTopic topic, string message);

    /// <summary>
    /// Resumes a stream that was interrupted (e.g., due to disconnection).
    /// </summary>
    /// <param name="topic">The topic to resume streaming in</param>
    /// <param name="streamingMessage">The current message state to continue from</param>
    /// <param name="startMessageId">The message ID to resume from</param>
    Task ResumeStreamResponseAsync(StoredTopic topic, ChatMessageModel streamingMessage, string startMessageId);
}
