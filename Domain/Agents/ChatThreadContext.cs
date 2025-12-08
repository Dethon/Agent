using System.Threading.Channels;
using Domain.DTOs;

namespace Domain.Agents;

public sealed class ChatThreadContext
{
    public CancellationTokenSource Cts { get; } = new();

    public Channel<ChatPrompt> PromptChannel { get; } = Channel
        .CreateBounded<ChatPrompt>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait
        });

    public CancellationTokenSource GetLinkedTokenSource(CancellationToken externalToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
    }

    public void Complete()
    {
        PromptChannel.Writer.TryComplete();
        Cts.Cancel();
        Cts.Dispose();
    }
}