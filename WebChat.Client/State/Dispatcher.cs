namespace WebChat.Client.State;

/// <summary>
/// Routes actions to registered handlers.
/// Supports multiple handlers per action type (stores + effects).
/// </summary>
public sealed class Dispatcher : IDispatcher
{
    private readonly Dictionary<Type, List<Action<IAction>>> _handlers = new();

    /// <summary>
    /// Register a handler for a specific action type.
    /// Multiple handlers can be registered for the same action.
    /// </summary>
    public void RegisterHandler<TAction>(Action<TAction> handler) where TAction : IAction
    {
        var actionType = typeof(TAction);
        if (!_handlers.TryGetValue(actionType, out var handlerList))
        {
            handlerList = [];
            _handlers[actionType] = handlerList;
        }
        handlerList.Add(action => handler((TAction)action));
    }

    /// <summary>
    /// Dispatch an action to all registered handlers.
    /// No-op if no handlers registered (fail-safe for optional handlers).
    /// </summary>
    public void Dispatch<TAction>(TAction action) where TAction : IAction
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_handlers.TryGetValue(typeof(TAction), out var handlerList))
        {
            foreach (var handler in handlerList)
            {
                handler(action);
            }
        }
    }
}
