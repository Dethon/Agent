using Domain.DTOs.WebChat;

namespace Tests.Unit.WebChat.Client.Fixtures;

// A stream that stays open until the test lets it end, so a topic really is mid-reply while
// the test asserts on it. A stream that completes at once takes the topic out of the
// streaming state before the assertion runs.
public sealed class GatedChatStream
{
    private readonly TaskCompletionSource _gate = new();

    // A transport that is already dead when the stream is opened: the fault surfaces on the
    // first pull, because nothing before it touches the wire.
    public Exception? FaultOnOpen { get; set; }

    public void Release() => _gate.TrySetResult();

    public async IAsyncEnumerable<ChatStreamMessage> Chunks()
    {
        if (FaultOnOpen is not null)
        {
            await Task.Yield();
            throw FaultOnOpen;
        }

        yield return new ChatStreamMessage { Content = "thinking", MessageId = "m-1" };
        await _gate.Task;
    }
}