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


    public TState State => _subject.Value;


    public IObservable<TState> StateObservable => _subject.AsObservable();


    public void Dispatch<TAction>(TAction action, Func<TState, TAction, TState> reducer)
        where TAction : IAction
    {
        var newState = reducer(State, action);
        _subject.OnNext(newState);
    }

    public void Dispose() => _subject.Dispose();
}