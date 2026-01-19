using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace WebChat.Client.State;

public sealed class Store<TState> : IDisposable where TState : class
{
    private readonly BehaviorSubject<TState> _subject;

    public Store(TState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        _subject = new BehaviorSubject<TState>(initialState);
    }

    /// <summary>
    /// Current state value. Use for synchronous reads.
    /// </summary>
    public TState State => _subject.Value;

    /// <summary>
    /// Observable state stream. Subscribe to receive state updates.
    /// New subscribers immediately receive current state (BehaviorSubject semantics).
    /// </summary>
    public IObservable<TState> StateObservable => _subject.AsObservable();

    /// <summary>
    /// Dispatch an action through a reducer to produce new state.
    /// </summary>
    public void Dispatch<TAction>(TAction action, Func<TState, TAction, TState> reducer)
        where TAction : IAction
    {
        var newState = reducer(State, action);
        _subject.OnNext(newState);
    }

    public void Dispose() => _subject.Dispose();
}
