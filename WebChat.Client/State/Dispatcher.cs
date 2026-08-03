namespace WebChat.Client.State;

public sealed class Dispatcher : IDispatcher
{
    private readonly List<Registration> _registrations = [];

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

    public void Dispatch<TAction>(TAction action) where TAction : IAction
    {
        ArgumentNullException.ThrowIfNull(action);

        // Snapshot first: a handler may register or dispose a registration while it runs.
        var handlers = _registrations
            .Where(r => r.ActionType is null || r.ActionType == typeof(TAction))
            .Select(r => r.Handler)
            .ToList();

        foreach (var handler in handlers)
        {
            handler(action);
        }
    }

    private IDisposable Register(Type? actionType, Action<IAction> handler)
    {
        var registration = new Registration(actionType, handler);
        _registrations.Add(registration);
        return new HandlerRegistration(_registrations, registration);
    }

    private sealed record Registration(Type? ActionType, Action<IAction> Handler);

    private sealed class HandlerRegistration(List<Registration> registrations, Registration registration)
        : IDisposable
    {
        public void Dispose() => registrations.Remove(registration);
    }
}