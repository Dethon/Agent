using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Approval;
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
        var stream = new GatedChatStream();
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
        var stream = new GatedChatStream();
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
        var stream = new GatedChatStream();
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

    [Fact]
    public async Task ADelete_ThatCouldNotBeMade_LeavesTheConversationInTheSidebar()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);

        client.GoNotLive();
        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        await TestChat.Eventually(() => client.Topics.State.Topics.Any(topic => topic.TopicId == "topic-1"));
        client.Topics.State.Topics.Single().Name.ShouldBe("Topic");
    }

    [Fact]
    public async Task ADelete_OfTheOpenConversation_ThatCouldNotBeMade_LeavesItSelected()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => client.Topics.State.SelectedTopicId == "topic-1");
        client.Topics.State.Topics.Single().TopicId.ShouldBe("topic-1");
    }

    [Fact]
    public async Task ADelete_WhileLive_StillRemovesTheConversation()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);

        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "DeleteTopic"));
        await TestChat.Eventually(() => client.Topics.State.Topics.Count == 0);
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACancel_ThatCouldNotBeMade_RaisesOneToastAndLeavesTheReplyRunning()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new CancelStreaming("topic-1"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Streaming.State.StreamingTopics.ShouldContain("topic-1");
        stream.Release();
    }

    [Fact]
    public async Task ACancel_WhileLive_StillStopsTheReply()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new CancelStreaming("topic-1"));

        await TestChat.Eventually(() => !client.Streaming.State.StreamingTopics.Contains("topic-1"));
        client.Toasts.State.Toasts.ShouldBeEmpty();
        stream.Release();
    }

    [Fact]
    public async Task AnApprovalAnswered_WhileNotLive_LeavesThePromptOnScreen()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new ShowApproval("topic-1", new ToolApprovalRequestMessage("approval-1", [])));

        client.GoNotLive();
        await client.Service<ApprovalResponder>().RespondAsync("approval-1", ToolApprovalResult.Approved);

        client.Approvals.State.CurrentRequest?.ApprovalId.ShouldBe("approval-1");
        client.Toasts.State.Toasts.Count.ShouldBe(1);
    }

    // A server that refuses is live and has answered — the approval is no longer pending, so
    // the prompt goes away exactly as it does today.
    [Fact]
    public async Task AnApprovalTheServerRefuses_StillClearsThePrompt()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        client.Dispatcher.Dispatch(new ShowApproval("topic-1", new ToolApprovalRequestMessage("approval-1", [])));
        transport.Answer("RespondToApprovalAsync", false);

        await client.Service<ApprovalResponder>().RespondAsync("approval-1", ToolApprovalResult.Approved);

        client.Approvals.State.CurrentRequest.ShouldBeNull();
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoFailedUserActions_InTheSameWindow_ProduceOneToast()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));
        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => client.Topics.State.Topics.Any(topic => topic.TopicId == "topic-1"));
        client.Toasts.State.Toasts.Count.ShouldBe(1);
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
}