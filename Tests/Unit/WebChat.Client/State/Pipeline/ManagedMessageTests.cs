using Shouldly;
using WebChat.Client.State.Pipeline;

namespace Tests.Unit.WebChat.Client.State.Pipeline;

public sealed class ManagedMessageTests
{
    [Fact]
    public void ManagedMessage_HasContent_WhenContentNotEmpty()
    {
        var message = new ManagedMessage
        {
            Id = "msg-1",
            TopicId = "topic-1",
            State = MessageLifecycle.Streaming,
            Role = "assistant",
            Content = "Hello"
        };

        message.HasContent.ShouldBeTrue();
    }

    [Fact]
    public void ManagedMessage_HasContent_WhenReasoningNotEmpty()
    {
        var message = new ManagedMessage
        {
            Id = "msg-1",
            TopicId = "topic-1",
            State = MessageLifecycle.Streaming,
            Role = "assistant",
            Reasoning = "Thinking..."
        };

        message.HasContent.ShouldBeTrue();
    }

    [Fact]
    public void ManagedMessage_HasNoContent_WhenAllEmpty()
    {
        var message = new ManagedMessage
        {
            Id = "msg-1",
            TopicId = "topic-1",
            State = MessageLifecycle.Pending,
            Role = "user"
        };

        message.HasContent.ShouldBeFalse();
    }
}
