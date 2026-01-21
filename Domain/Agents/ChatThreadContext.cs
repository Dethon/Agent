namespace Domain.Agents;

public sealed class ChatThreadContext : IDisposable
{
    private int _isDisposed;
    private Action? _onComplete;

    public CancellationTokenSource Cts { get; } = new();

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        _onComplete?.Invoke();
        _onComplete = null;

        Cts.Cancel();
        Cts.Dispose();
    }

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
}