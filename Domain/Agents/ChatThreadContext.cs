using System.Threading.Channels;
using Domain.DTOs;

namespace Domain.Agents;

public sealed class ChatThreadContext
{
    public CancellationTokenSource Cts { get; } = new();

    public CancellationTokenSource GetLinkedTokenSource(CancellationToken externalToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
    }

    public void Complete()
    {
        Cts.Cancel();
        Cts.Dispose();
    }
}