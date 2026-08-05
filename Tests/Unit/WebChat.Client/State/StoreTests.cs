using System.Reactive.Linq;
using Shouldly;
using WebChat.Client.State;

namespace Tests.Unit.WebChat.Client.State;

public class StoreTests
{
    private sealed record CounterState(int Count);

    private sealed record Increment : IAction;

    [Fact]
    public void Dispatch_ReducerReturnsTheStateItWasPassed_EmitsNothing()
    {
        using var store = new Store<CounterState>(new CounterState(0));
        var emissions = new List<CounterState>();
        using var subscription = store.StateObservable.Skip(1).Subscribe(emissions.Add);

        store.Dispatch(new Increment(), (state, _) => state);

        emissions.ShouldBeEmpty();
    }

    [Fact]
    public void Dispatch_ReducerReturnsNewInstanceEqualByValue_Emits()
    {
        using var store = new Store<CounterState>(new CounterState(0));
        var emissions = new List<CounterState>();
        using var subscription = store.StateObservable.Skip(1).Subscribe(emissions.Add);

        store.Dispatch(new Increment(), (state, _) => new CounterState(state.Count));

        emissions.ShouldHaveSingleItem().Count.ShouldBe(0);
    }
}