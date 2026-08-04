using Shouldly;

namespace Tests.Unit.WebChat.Client.Fixtures;

public class FakeHubConnectionTests
{
    private readonly FakeHubConnection _connection = new();

    [Fact]
    public void Raise_HandlerRegisteredForThatWireName_InvokesIt()
    {
        var received = new List<string>();
        _connection.On<string>("OnThing", received.Add);

        _connection.Raise("OnThing", "payload");

        received.ShouldBe(["payload"]);
    }

    [Fact]
    public void Raise_RegistrationDisposed_HandlerNoLongerRuns()
    {
        var received = new List<string>();
        var registration = _connection.On<string>("OnThing", received.Add);

        registration.Dispose();
        _connection.Raise("OnThing", "payload");

        received.ShouldBeEmpty();
        _connection.BoundWireNames.ShouldBeEmpty();
    }

    [Fact]
    public void Raise_NothingRegisteredForThatWireName_DoesNothing()
    {
        var received = new List<string>();
        _connection.On<string>("OnThing", received.Add);

        _connection.Raise("OnOtherThing", "payload");

        received.ShouldBeEmpty();
    }
}