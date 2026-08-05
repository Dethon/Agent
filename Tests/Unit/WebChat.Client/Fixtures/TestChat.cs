using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.WebChat.Client.Fixtures;

public static class TestChat
{
    public static TopicMetadata Topic(
        string topicId, long chatId = 10, long threadId = 20, string agentId = "agent-1") =>
        new(topicId, chatId, threadId, agentId, "Topic", DateTimeOffset.UnixEpoch, null);

    public static ChatHistoryMessage HistoryMessage(string messageId, string content) =>
        new(messageId, "assistant", content, null, DateTimeOffset.UnixEpoch);

    // Dispatch is fire-and-forget, so a test that dispatches instead of awaiting an entry
    // point has to wait for the state the effect produces.
    public static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue("the expected state was not reached within the timeout");
    }
}