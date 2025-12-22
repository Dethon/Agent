using System.Text.Json;
using Domain.Contracts;

namespace Domain.Agents;

public sealed class ChatThreadContext : IAsyncDisposable
{
    private readonly AgentKey _key;
    private readonly IThreadStateStore _store;
    private Action? _onComplete;
    private int _isDisposed;

    public CancellationTokenSource Cts { get; } = new();
    public JsonElement? PersistedThread { get; private set; }

    private ChatThreadContext(AgentKey key, IThreadStateStore store, JsonElement? persistedThread)
    {
        _key = key;
        _store = store;
        PersistedThread = persistedThread;
    }

    public static async Task<ChatThreadContext> CreateAsync(
        AgentKey key,
        IThreadStateStore store,
        CancellationToken ct)
    {
        var persistedThread = await store.LoadAsync(key, ct);
        return new ChatThreadContext(key, store, persistedThread);
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

    public async Task SaveThreadAsync(JsonElement serializedThread, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        await _store.SaveAsync(_key, serializedThread, ct);
        PersistedThread = serializedThread;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        _onComplete?.Invoke();
        _onComplete = null;

        await _store.DeleteAsync(_key, CancellationToken.None);

        await Cts.CancelAsync();
        Cts.Dispose();
    }
}