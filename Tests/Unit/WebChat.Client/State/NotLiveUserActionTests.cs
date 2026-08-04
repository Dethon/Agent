using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// A call the user initiated raises one error toast when it could not be made. The toast store
// suppresses a repeat of the same message, so a resume with several failures is one toast.
public sealed class NotLiveUserActionTests
{
    [Fact]
    public async Task AMessageTyped_WhileNotLive_RaisesOneToastAndAddsNoMessage()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Messages.State.MessagesByTopic.GetValueOrDefault("topic-1", []).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANewConversation_StartedWhileNotLive_RaisesOneToastAndAddsNoTopic()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage(null, "hello"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Topics.State.Topics.ShouldBeEmpty();
    }

    // The second half of the same defect: the send goes through the enqueue call, and a false
    // from that means "there is no stream to enqueue onto". Not live must not read as false,
    // or the client opens a stream that has already failed and announces it.
    [Fact]
    public async Task AnEnqueue_ThatCouldNotBeMade_OpensNoNewStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedStream();
        transport.Answer("SendMessage", _ => stream.Chunks());

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
        stream.Release();
    }

    [Fact]
    public async Task AServerAnsweringNoToTheEnqueue_StillOpensANewStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        transport.Answer("EnqueueMessage", false);

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "SendMessage") == 2);
        client.Toasts.State.Toasts.ShouldBeEmpty();
        stream.Release();
    }

    [Fact]
    public async Task AServerAnsweringYesToTheEnqueue_StillOpensNoNewStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        transport.Answer("EnqueueMessage", true);

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "EnqueueMessage"));
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
        client.Toasts.State.Toasts.ShouldBeEmpty();
        stream.Release();
    }

    [Fact]
    public async Task AMessage_SentWhileLive_StillReachesTheServerAndTheTranscript()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        transport.Answer("SendMessage", _ => Chunks(new ChatStreamMessage { Content = "hi", IsComplete = true }));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        await TestChat.Eventually(() =>
            client.Messages.State.MessagesByTopic.GetValueOrDefault("topic-1", []).Any(m => m.Role == "user"));
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    private static void SeedTopic(ScriptedChatClient client)
    {
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopic.FromMetadata(TestChat.Topic("topic-1"))]));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
    }

    private static async IAsyncEnumerable<ChatStreamMessage> Chunks(params ChatStreamMessage[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
            await Task.Yield();
        }
    }

    // A stream that stays open until the test lets it end, so a second send lands on a topic
    // that really is mid-reply.
    private sealed class GatedStream
    {
        private readonly TaskCompletionSource _gate = new();

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<ChatStreamMessage> Chunks()
        {
            yield return new ChatStreamMessage { Content = "thinking", MessageId = "m-1" };
            await _gate.Task;
        }
    }
}