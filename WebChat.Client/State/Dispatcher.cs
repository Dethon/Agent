namespace WebChat.Client.State;

public sealed class Dispatcher : IDispatcher
{
    private readonly List<Registration> _catchAll = [];
    private readonly Dictionary<Type, List<Registration>> _typed = new();
    private long _nextSequence;

    public IDisposable RegisterHandler<TAction>(Action<TAction> handler) where TAction : IAction
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(typeof(TAction), action => handler((TAction)action));
    }

    public IDisposable RegisterCatchAll(Action<IAction> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Register(actionType: null, handler);
    }

    // Matched on the runtime type, never the caller's static type: an action reached through
    // an IAction-typed variable still has to find the handler registered for what it actually
    // is, or a typed effect handler silently goes dark behind whatever variable carried it here.
    public void Dispatch<TAction>(TAction action) where TAction : IAction
    {
        ArgumentNullException.ThrowIfNull(action);

        var actionType = action.GetType();
        var typed = _typed.TryGetValue(actionType, out var list) ? list : [];

        // Snapshot first: a handler may register or dispose a registration while it runs. Only
        // the two buckets that can possibly match this action are touched — catch-all plus this
        // one action type — instead of scanning every registration on every dispatch.
        var handlers = _catchAll.Count == 0 && typed.Count == 0
            ? []
            : _catchAll.Concat(typed).OrderBy(r => r.Sequence).Select(r => r.Handler).ToList();

        foreach (var handler in handlers)
        {
            handler(action);
        }
    }

    private IDisposable Register(Type? actionType, Action<IAction> handler)
    {
        var registration = new Registration(actionType, handler, _nextSequence++);

        var bucket = actionType is null
            ? _catchAll
            : _typed.TryGetValue(actionType, out var list) ? list : _typed[actionType] = [];
        bucket.Add(registration);

        return new HandlerRegistration(bucket, registration);
    }

    private sealed record Registration(Type? ActionType, Action<IAction> Handler, long Sequence);

    private sealed class HandlerRegistration(List<Registration> bucket, Registration registration)
        : IDisposable
    {
        public void Dispose() => bucket.Remove(registration);
    }
}