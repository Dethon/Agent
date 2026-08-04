using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.WebChat.Client.Fixtures;

// Proof that the fixture is wired, in both directions. Everything else about this feature is
// asserted on the state a user would see; these two are about the composition itself.
public sealed class ScriptedChatClientTests
{
    [Fact]
    public async Task AServerPush_ThroughTheScriptedTransport_ReachesTheStore()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();

        transport.Raise("OnTopicChanged", new TopicChangedNotification(
            TopicChangeType.Created, "topic-1", TestChat.Topic("topic-1")));

        client.Topics.State.Topics.ShouldContain(topic => topic.TopicId == "topic-1");
    }

    [Fact]
    public async Task AHubCall_ThroughALiveTransport_ArrivesWithTheCallersArguments()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();

        await client.LiveConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "agent-1", "hearth");

        var call = transport.Calls.Single(c => c.MethodName == "GetAllTopics");
        call.Arguments.ShouldBe(["agent-1", "hearth"]);
    }

    [Fact]
    public async Task AHubCall_ThroughATransportThatIsNotLive_NeverReachesIt()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        client.GoNotLive();

        var result = await client.LiveConnection.InvokeAsync<IReadOnlyList<TopicMetadata>>(
            "GetAllTopics", "agent-1", "hearth");

        result.IsLive.ShouldBeFalse();
        transport.Calls.ShouldBeEmpty();
    }
}