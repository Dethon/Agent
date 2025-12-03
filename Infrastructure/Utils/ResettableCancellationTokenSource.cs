namespace Infrastructure.Utils;

internal sealed class ResettableCancellationTokenSource : IDisposable
{
    private CancellationTokenSource _cts = new();
    private bool _isDisposed;

    public CancellationToken Token => _cts.Token;

    public CancellationTokenSource CreateLinkedTokenSource(CancellationToken other)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(other, _cts.Token);
    }

    public void CancelAndReset()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        oldCts.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}