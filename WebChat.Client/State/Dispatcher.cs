namespace WebChat.Client.State;

public sealed class Dispatcher : IDispatcher
{
    private readonly Dictionary<Type, List<Action<IAction>>> _handlers = new();

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