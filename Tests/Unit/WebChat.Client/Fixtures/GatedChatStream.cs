using System.Threading.Channels;
using Domain.DTOs.WebChat;

namespace Tests.Unit.WebChat.Client.Fixtures;

// A stream that stays open until the test lets it end, so a topic really is mid-reply while
// the test asserts on it. A stream that completes at once takes the topic out of the
// streaming state before the assertion runs. A test can also write further chunks into it,
// for the cases where something else has to reach the client while the reply is being written.
public sealed class GatedChatStream
{
    private readonly Channel<ChatStreamMessage> _chunks = Channel.CreateUnbounded<ChatStreamMessage>();

    // A transport that is already dead when the stream is opened: the fault surfaces on the
    // first pull, because nothing before it touches the wire.
    public Exception? FaultOnOpen { get; set; }

    public void Write(ChatStreamMessage chunk) => _chunks.Writer.TryWrite(chunk);

    public void Release() => _chunks.Writer.TryComplete();

    public async IAsyncEnumerable<ChatStreamMessage> Chunks()
    {
        if (FaultOnOpen is not null)
        {
            await Task.Yield();
            throw FaultOnOpen;
        }

        yield return new ChatStreamMessage { Content = "thinking", MessageId = "m-1" };

        await foreach (var chunk in _chunks.Reader.ReadAllAsync())
        {
            yield return chunk;
        }
    }
}