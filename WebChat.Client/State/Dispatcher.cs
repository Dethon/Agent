namespace WebChat.Client.State;

public sealed class Dispatcher : IDispatcher
{
    private readonly Dictionary<Type, List<Action<IAction>>> _handlers = new();

    public IDisposable RegisterHandler<TAction>(Action<TAction> handler) where TAction : IAction
    {
        var actionType = typeof(TAction);
        if (!_handlers.TryGetValue(actionType, out var handlerList))
        {
            handlerList = [];
            _handlers[actionType] = handlerList;
        }

        Action<IAction> wrapped = action => handler((TAction)action);
        handlerList.Add(wrapped);
        return new HandlerRegistration(handlerList, wrapped);
    }

    private sealed class HandlerRegistration(List<Action<IAction>> list, Action<IAction> handler) : IDisposable
    {
        public void Dispose() => list.Remove(handler);
    }

    public void Dispatch<TAction>(TAction action) where TAction : IAction
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!_handlers.TryGetValue(typeof(TAction), out var handlerList))
        {
            return;
        }

        foreach (var handler in handlerList)
        {
            handler(action);
        }
    }
}