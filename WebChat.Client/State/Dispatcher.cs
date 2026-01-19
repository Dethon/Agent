namespace WebChat.Client.State;

/// <summary>
/// Routes actions to registered handlers.
/// In Phase 1, this is a simple implementation. Middleware support can be added later.
/// </summary>
public sealed class Dispatcher : IDispatcher
{
    private readonly Dictionary<Type, Action<IAction>> _handlers = new();

    /// <summary>
    /// Register a handler for a specific action type.
    /// Called during store initialization to wire up reducers.
    /// </summary>
    public void RegisterHandler<TAction>(Action<TAction> handler) where TAction : IAction
    {
        _handlers[typeof(TAction)] = action => handler((TAction)action);
    }

    /// <summary>
    /// Dispatch an action to its registered handler.
    /// No-op if no handler registered (fail-safe for optional handlers).
    /// </summary>
    public void Dispatch<TAction>(TAction action) where TAction : IAction
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_handlers.TryGetValue(typeof(TAction), out var handler))
        {
            handler(action);
        }
    }
}
