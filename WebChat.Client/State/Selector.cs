namespace WebChat.Client.State;

/// <typeparam name="TState">The input state type</typeparam>
/// <typeparam name="TResult">The derived result type</typeparam>
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
    /// <example>
    /// var topicCountSelector = Selector.Create((TopicsState s) => s.Topics.Count);
    /// int count = topicCountSelector.Select(store.State);
    /// </example>
    public static Selector<TState, TResult> Create<TState, TResult>(
        Func<TState, TResult> projector) where TState : class
        => new(projector);


    /// <example>
    /// var topicsSelector = Selector.Create((TopicsState s) => s.Topics);
    /// var activeTopicsSelector = Selector.Compose(
    ///     topicsSelector,
    ///     topics => topics.Where(t => t.IsActive).ToList()
    /// );
    /// </example>
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