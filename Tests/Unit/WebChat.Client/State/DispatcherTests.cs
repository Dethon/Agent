using Shouldly;
using WebChat.Client.State;

namespace Tests.Unit.WebChat.Client.State;

public class DispatcherTests
{
    private sealed record Increment : IAction;

    private sealed record Unhandled : IAction;

    [Fact]
    public void Dispatch_ActionTypedAsIAction_ReachesCatchAll()
    {
        var dispatcher = new Dispatcher();
        var received = new List<IAction>();
        using var registration = dispatcher.RegisterCatchAll(received.Add);

        IAction action = new Increment();
        dispatcher.Dispatch(action);

        received.ShouldHaveSingleItem().ShouldBeOfType<Increment>();
    }

    // The static type at the call site must not decide this: a dispatch reached through an
    // IAction-typed variable still carries an Increment at runtime, and the typed handler
    // exists to react to exactly that.
    [Fact]
    public void Dispatch_ActionTypedAsIAction_StillReachesTheTypedHandler()
    {
        var dispatcher = new Dispatcher();
        var received = new List<string>();
        using var registration = dispatcher.RegisterHandler<Increment>(_ => received.Add("typed"));

        IAction action = new Increment();
        dispatcher.Dispatch(action);

        received.ShouldBe(["typed"]);
    }

    [Fact]
    public void Dispatch_ActionWithNoTypedHandler_ReachesCatchAll()
    {
        var dispatcher = new Dispatcher();
        var received = new List<IAction>();
        using var registration = dispatcher.RegisterCatchAll(received.Add);

        dispatcher.Dispatch(new Increment());
        dispatcher.Dispatch(new Unhandled());

        received.Select(a => a.GetType()).ShouldBe([typeof(Increment), typeof(Unhandled)]);
    }

    [Fact]
    public void Dispatch_CatchAllRegisteredFirst_RunsBeforeTypedHandler()
    {
        var dispatcher = new Dispatcher();
        var order = new List<string>();
        using var catchAll = dispatcher.RegisterCatchAll(_ => order.Add("catch-all"));
        using var typed = dispatcher.RegisterHandler<Increment>(_ => order.Add("typed"));

        dispatcher.Dispatch(new Increment());

        order.ShouldBe(["catch-all", "typed"]);
    }

    [Fact]
    public void Dispatch_TypedHandlerRegisteredFirst_RunsBeforeCatchAll()
    {
        var dispatcher = new Dispatcher();
        var order = new List<string>();
        using var typed = dispatcher.RegisterHandler<Increment>(_ => order.Add("typed"));
        using var catchAll = dispatcher.RegisterCatchAll(_ => order.Add("catch-all"));

        dispatcher.Dispatch(new Increment());

        order.ShouldBe(["typed", "catch-all"]);
    }

    [Fact]
    public void Dispatch_AfterCatchAllDisposed_DoesNotReachHandler()
    {
        var dispatcher = new Dispatcher();
        var received = new List<IAction>();
        var registration = dispatcher.RegisterCatchAll(received.Add);

        dispatcher.Dispatch(new Increment());
        registration.Dispose();
        dispatcher.Dispatch(new Increment());

        received.Count.ShouldBe(1);
    }

    [Fact]
    public void Dispatch_AfterTypedHandlerDisposed_DoesNotReachHandler()
    {
        var dispatcher = new Dispatcher();
        var received = new List<IAction>();
        var registration = dispatcher.RegisterHandler<Increment>(received.Add);

        dispatcher.Dispatch(new Increment());
        registration.Dispose();
        dispatcher.Dispatch(new Increment());

        received.Count.ShouldBe(1);
    }
}