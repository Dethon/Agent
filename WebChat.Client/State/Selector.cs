namespace WebChat.Client.State;

public sealed class Selector<TState, TResult> where TState : class
{
    private readonly Func<TState, TResult> _projector;
    private TState? _lastState;
    private TResult? _cachedResult;
    private bool _hasValue;

    public Selector(Func<TState, TResult> projector)
    {
        ArgumentNullException.ThrowIfNull(projector);
        _projector = projector;
    }


    public TResult Select(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Reference equality check - records create new instances on mutation
        if (_hasValue && ReferenceEquals(_lastState, state))
        {
            return _cachedResult!;
        }

        _lastState = state;
        _cachedResult = _projector(state);
        _hasValue = true;
        return _cachedResult;
    }


    public void Invalidate()
    {
        _lastState = null;
        _cachedResult = default;
        _hasValue = false;
    }
}

public static class Selector
{
    public static Selector<TState, TResult> Create<TState, TResult>(Func<TState, TResult> projector)
        where TState : class
    {
        return new Selector<TState, TResult>(projector);
    }

    public static Selector<TState, TFinal> Compose<TState, TIntermediate, TFinal>(
        Selector<TState, TIntermediate> first,
        Func<TIntermediate, TFinal> second) where TState : class
    {
        // Compose into a single selector - memoization happens at the final level
        return new Selector<TState, TFinal>(state =>
        {
            var intermediate = first.Select(state);
            return second(intermediate);
        });
    }
}