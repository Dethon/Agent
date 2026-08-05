using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.Services;

// A hub call comes back with the server's answer or with not live. The states that answer not
// live are the point: the old guards checked only for a missing connection, and a connection
// that is connecting or reconnecting is present and still cannot carry a call.
public sealed class ChatLiveConnectionHubCallTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly FakeHubConnectionFactory _factory = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly ConnectionStore _connectionStore;
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly ChatLiveConnection _liveConnection;

    public ChatLiveConnectionHubCallTests()
    {
        _connectionStore = new ConnectionStore(_dispatcher);
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);

        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, _streamingStore, NullLogger<MessagePipeline>.Instance);
        var hubEventDispatcher = new HubEventDispatcher(
            _dispatcher, _topicsStore, _streamingStore, pipeline);

        _liveConnection = new ChatLiveConnection(
            _factory,
            new HubEventBinder(hubEventDispatcher),
            new ConnectionEventDispatcher(_dispatcher),
            _timeProvider);
    }

    [Fact]
    public async Task InvokeAsync_Live_AnswersTheServersAnswer()
    {
        var connection = await ConnectAsync();
        connection.Answer("GetCount", 7);

        var result = await _liveConnection.InvokeAsync<int>("GetCount");

        result.IsLive.ShouldBeTrue();
        result.Value.ShouldBe(7);
    }

    [Fact]
    public async Task InvokeAsync_Live_PassesTheArgumentsThrough()
    {
        var connection = await ConnectAsync();
        connection.Answer("GetCount", 0);

        await _liveConnection.InvokeAsync<int>("GetCount", "agent-1", 42L);

        var call = connection.Calls.Single();
        call.MethodName.ShouldBe("GetCount");
        call.Arguments.ShouldBe(["agent-1", 42L]);
    }

    [Fact]
    public async Task InvokeAsync_NoConnection_AnswersNotLive()
    {
        var result = await _liveConnection.InvokeAsync<int>("GetCount");

        result.IsLive.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HubConnectionState.Connecting)]
    [InlineData(HubConnectionState.Reconnecting)]
    [InlineData(HubConnectionState.Disconnected)]
    public async Task InvokeAsync_ConnectionNotUp_AnswersNotLive(HubConnectionState state)
    {
        var connection = await ConnectAsync();
        connection.Answer("GetCount", 7);
        connection.State = state;

        var result = await _liveConnection.InvokeAsync<int>("GetCount");

        result.IsLive.ShouldBeFalse();
        connection.Calls.ShouldBeEmpty();
    }

    // The transport can die after the state check and before the answer arrives. That window
    // is a not-live window like any other, so the call answers not live instead of throwing.
    public static TheoryData<Exception> TransportFaults => new()
    {
        new InvalidOperationException(
            "The 'InvokeCoreAsync' method cannot be called if the connection is not active"),
        new TaskCanceledException("Invocation canceled: the connection closed mid-call"),
        new ObjectDisposedException(nameof(HubConnection))
    };

    [Theory]
    [MemberData(nameof(TransportFaults))]
    public async Task InvokeAsync_TransportDiesMidCall_AnswersNotLive(Exception fault)
    {
        var connection = await ConnectAsync();
        connection.Answer("GetCount", _ => throw fault);

        var result = await _liveConnection.InvokeAsync<int>("GetCount");

        result.IsLive.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(TransportFaults))]
    public async Task VoidInvokeAsync_TransportDiesMidCall_AnswersNotLive(Exception fault)
    {
        var connection = await ConnectAsync();
        connection.Answer("SaveTopic", _ => throw fault);

        var result = await _liveConnection.InvokeAsync("SaveTopic", "topic-1", true);

        result.IsLive.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(TransportFaults))]
    public async Task StreamAsync_TransportDiesMidCall_AnswersNotLive(Exception fault)
    {
        var connection = await ConnectAsync();
        connection.Answer("Count", _ => throw fault);

        var result = await _liveConnection.StreamAsync<int>("Count");

        result.IsLive.ShouldBeFalse();
    }

    // A HubException is the server answering: the call arrived and the handler faulted it.
    // Mapping that to not live would tell the user to retry a call the server already refused.
    [Fact]
    public async Task InvokeAsync_TheServerFaultsTheCall_PropagatesTheHubException()
    {
        var connection = await ConnectAsync();
        connection.Answer("GetCount", _ => throw new HubException("handler failed"));

        await Should.ThrowAsync<HubException>(() => _liveConnection.InvokeAsync<int>("GetCount"));
    }

    [Fact]
    public async Task VoidInvokeAsync_Live_ReachesTheTransport()
    {
        var connection = await ConnectAsync();

        var result = await _liveConnection.InvokeAsync("SaveTopic", "topic-1", true);

        result.IsLive.ShouldBeTrue();
        connection.Calls.Single().MethodName.ShouldBe("SaveTopic");
    }

    [Theory]
    [InlineData(HubConnectionState.Connecting)]
    [InlineData(HubConnectionState.Reconnecting)]
    [InlineData(HubConnectionState.Disconnected)]
    public async Task VoidInvokeAsync_ConnectionNotUp_AnswersNotLive(HubConnectionState state)
    {
        var connection = await ConnectAsync();
        connection.State = state;

        var result = await _liveConnection.InvokeAsync("SaveTopic", "topic-1", true);

        result.IsLive.ShouldBeFalse();
        connection.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task VoidInvokeAsync_NoConnection_AnswersNotLive()
    {
        var result = await _liveConnection.InvokeAsync("SaveTopic", "topic-1", true);

        result.IsLive.ShouldBeFalse();
    }

    [Fact]
    public async Task StreamAsync_Live_AnswersTheServersStream()
    {
        var connection = await ConnectAsync();
        connection.Answer("Count", Numbers(1, 2, 3));

        var result = await _liveConnection.StreamAsync<int>("Count");

        result.IsLive.ShouldBeTrue();
        var seen = new List<int>();
        await foreach (var value in result.Value!)
        {
            seen.Add(value);
        }

        seen.ShouldBe([1, 2, 3]);
    }

    [Theory]
    [InlineData(HubConnectionState.Connecting)]
    [InlineData(HubConnectionState.Reconnecting)]
    [InlineData(HubConnectionState.Disconnected)]
    public async Task StreamAsync_ConnectionNotUp_AnswersNotLiveBeforeAnyIteration(HubConnectionState state)
    {
        var connection = await ConnectAsync();
        connection.Answer("Count", Numbers(1, 2, 3));
        connection.State = state;

        var result = await _liveConnection.StreamAsync<int>("Count");

        result.IsLive.ShouldBeFalse();
        connection.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task StreamAsync_NoConnection_AnswersNotLive()
    {
        var result = await _liveConnection.StreamAsync<int>("Count");

        result.IsLive.ShouldBeFalse();
    }

    private async Task<FakeHubConnection> ConnectAsync()
    {
        await _liveConnection.ConnectAsync();
        return _factory.Created.Single();
    }

    private static async IAsyncEnumerable<int> Numbers(params int[] values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    public void Dispose()
    {
        _connectionStore.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
    }
}