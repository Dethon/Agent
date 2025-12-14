namespace Domain.Agents;

public sealed class ChatThreadContext
{
    private Action? _onComplete;
    
    public CancellationTokenSource Cts { get; } = new();

    public void RegisterCompletionCallback(Action onComplete)
    {
        _onComplete = onComplete;
    }

    public CancellationTokenSource GetLinkedTokenSource(CancellationToken externalToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, externalToken);
    }

    public void Complete()
    {
        _onComplete?.Invoke();
        Cts.Cancel();
        Cts.Dispose();
    }
}