using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;

namespace Tests.Unit.WebChat.Client;

public sealed class MessagesReducersTests
{
    [Fact]
    public void MessagesLoaded_PopulatesFinalizedMessageIds()
    {
        var state = MessagesState.Initial;
        var messages = new List<ChatMessageModel>
        {
            new() { MessageId = "msg-1", Role = "user", Content = "Hello" },
            new() { MessageId = "msg-2", Role = "assistant", Content = "Hi" },
            new() { MessageId = null, Role = "user", Content = "No ID" }
        };

        var newState = MessagesReducers.Reduce(state, new MessagesLoaded("topic-1", messages));

        newState.FinalizedMessageIdsByTopic.ShouldContainKey("topic-1");
        var finalizedIds = newState.FinalizedMessageIdsByTopic["topic-1"];
        finalizedIds.ShouldContain("msg-1");
        finalizedIds.ShouldContain("msg-2");
        finalizedIds.Count.ShouldBe(2);
    }

    [Fact]
    public void MessagesLoaded_WithNoMessageIds_CreatesEmptySet()
    {
        var state = MessagesState.Initial;
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" }
        };

        var newState = MessagesReducers.Reduce(state, new MessagesLoaded("topic-1", messages));

        newState.FinalizedMessageIdsByTopic.ShouldContainKey("topic-1");
        newState.FinalizedMessageIdsByTopic["topic-1"].ShouldBeEmpty();
    }

    [Fact]
    public void AddMessage_SkipsIfMessageIdAlreadyFinalized()
    {
        var state = MessagesState.Initial with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>
            {
                ["topic-1"] = new List<ChatMessageModel>
                {
                    new() { MessageId = "msg-1", Role = "assistant", Content = "Existing" }
                }
            },
            FinalizedMessageIdsByTopic = new Dictionary<string, IReadOnlySet<string>>
            {
                ["topic-1"] = new HashSet<string> { "msg-1" }
            }
        };

        var newMessage = new ChatMessageModel { MessageId = "msg-1", Role = "assistant", Content = "Duplicate" };
        var newState = MessagesReducers.Reduce(state, new AddMessage("topic-1", newMessage, "msg-1"));

        newState.MessagesByTopic["topic-1"].Count.ShouldBe(1);
    }

    [Fact]
    public void UpdateMessage_FindsAndUpdatesMessageById()
    {
        var state = MessagesState.Initial with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>
            {
                ["topic-1"] = new List<ChatMessageModel>
                {
                    new() { MessageId = "msg-1", Role = "assistant", Content = "Original" },
                    new() { MessageId = "msg-2", Role = "assistant", Content = "Other" }
                }
            }
        };

        var updated = new ChatMessageModel { MessageId = "msg-1", Role = "assistant", Content = "Updated" };
        var newState = MessagesReducers.Reduce(state, new UpdateMessage("topic-1", "msg-1", updated));

        var messages = newState.MessagesByTopic["topic-1"];
        messages[0].Content.ShouldBe("Updated");
        messages[1].Content.ShouldBe("Other");
    }

    [Fact]
    public void UpdateMessage_WhenMessageIdNotFound_NoChange()
    {
        var state = MessagesState.Initial with
        {
            MessagesByTopic = new Dictionary<string, IReadOnlyList<ChatMessageModel>>
            {
                ["topic-1"] = new List<ChatMessageModel>
                {
                    new() { MessageId = "msg-1", Role = "assistant", Content = "Original" }
                }
            }
        };

        var updated = new ChatMessageModel { MessageId = "msg-99", Role = "assistant", Content = "Updated" };
        var newState = MessagesReducers.Reduce(state, new UpdateMessage("topic-1", "msg-99", updated));

        newState.MessagesByTopic["topic-1"][0].Content.ShouldBe("Original");
    }
}
