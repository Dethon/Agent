namespace Domain.Agents;

public sealed class ChatThreadContext : IDisposable
{
    private Action? _onComplete;
    private int _disposed;
    
    public CancellationTokenSource Cts { get; } = new();

    public void RegisterCompletionCallback(Action onComplete)
    {
        _onComplete = onComplete;
    }

    public CancellationTokenSource GetLinkedTokenSource(CancellationToken externalToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        if (_onComplete != null)
        {
            _onComplete.Invoke();
            _onComplete = null;
        }

        Cts.Cancel();
        Cts.Dispose();
    }
}