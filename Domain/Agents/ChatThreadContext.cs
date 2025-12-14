namespace Domain.Agents;

public sealed class ChatThreadContext : IDisposable
{
    private Action? _onComplete;
    private int _isDisposed;
    
    public CancellationTokenSource Cts { get; } = new();

    public void RegisterCompletionCallback(Action onComplete)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        _onComplete = onComplete;
    }

    public CancellationTokenSource GetLinkedTokenSource(CancellationToken externalToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        return CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
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