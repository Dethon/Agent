using Domain.DTOs.WebChat;

namespace Tests.Unit.WebChat.Client.Fixtures;

// A stream that stays open until the test lets it end, so a topic really is mid-reply while
// the test asserts on it. A stream that completes at once takes the topic out of the
// streaming state before the assertion runs.
public sealed class GatedChatStream
{
    private readonly TaskCompletionSource _gate = new();

    public void Release() => _gate.TrySetResult();

    public async IAsyncEnumerable<ChatStreamMessage> Chunks()
    {
        yield return new ChatStreamMessage { Content = "thinking", MessageId = "m-1" };
        await _gate.Task;
    }
}