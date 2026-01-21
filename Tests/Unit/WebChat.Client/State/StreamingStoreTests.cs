using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Streaming;

namespace Tests.Unit.WebChat.Client.State;

public class StreamingStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly StreamingStore _store;

    public StreamingStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new StreamingStore(_dispatcher);
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    [Fact]
    public void StreamStarted_AddsTopicToStreamingTopics()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        _store.State.StreamingTopics.ShouldContain("topic-1");
    }

    [Fact]
    public void StreamStarted_InitializesEmptyStreamingContent()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));

        _store.State.StreamingByTopic.ShouldContainKey("topic-1");
        var content = _store.State.StreamingByTopic["topic-1"];
        content.Content.ShouldBe("");
        content.Reasoning.ShouldBeNull();
        content.ToolCalls.ShouldBeNull();
        content.CurrentMessageId.ShouldBeNull();
        content.IsError.ShouldBeFalse();
    }

    [Fact]
    public void StreamChunk_ReplacesContentWithFullAccumulatedValue()
    {
        // The service accumulates content and sends full value in each chunk
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello ", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello World", null, null, "msg-1"));

        var content = _store.State.StreamingByTopic["topic-1"];
        content.Content.ShouldBe("Hello World");
    }

    [Fact]
    public void StreamChunk_ReplacesReasoningAndContentSeparately()
    {
        // The service accumulates and sends full values in each chunk
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", "Thinking ", null, "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello World", "Thinking about this", null, "msg-1"));

        var content = _store.State.StreamingByTopic["topic-1"];
        content.Content.ShouldBe("Hello World");
        content.Reasoning.ShouldBe("Thinking about this");
    }

    [Fact]
    public void StreamChunk_ReplacesToolCallsWithFullValue()
    {
        // The service accumulates and sends full values in each chunk
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Content", null, "tool1()", "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Content", null, "tool1()\ntool2()", "msg-1"));

        var content = _store.State.StreamingByTopic["topic-1"];
        content.Content.ShouldBe("Content");
        content.ToolCalls.ShouldBe("tool1()\ntool2()");
    }

    [Fact]
    public void StreamChunk_UpdatesCurrentMessageId()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));

        _store.State.StreamingByTopic["topic-1"].CurrentMessageId.ShouldBe("msg-1");

        _dispatcher.Dispatch(new StreamChunk("topic-1", " World", null, null, "msg-2"));

        _store.State.StreamingByTopic["topic-1"].CurrentMessageId.ShouldBe("msg-2");
    }

    [Fact]
    public void StreamCompleted_RemovesTopicFromStreamingByTopic()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamCompleted("topic-1"));

        _store.State.StreamingByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public void StreamCompleted_RemovesTopicFromStreamingTopics()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamCompleted("topic-1"));

        _store.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    [Fact]
    public void StreamCancelled_RemovesTopicFromStreamingByTopic()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamCancelled("topic-1"));

        _store.State.StreamingByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public void StreamCancelled_RemovesTopicFromStreamingTopics()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamCancelled("topic-1"));

        _store.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    [Fact]
    public void StreamError_SetsIsErrorTrue()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamError("topic-1", "Something went wrong"));

        var content = _store.State.StreamingByTopic["topic-1"];
        content.IsError.ShouldBeTrue();
        content.Content.ShouldBe("Hello"); // Content preserved
    }

    [Fact]
    public void StartResuming_AddsTopicToResumingTopics()
    {
        _dispatcher.Dispatch(new StartResuming("topic-1"));

        _store.State.ResumingTopics.ShouldContain("topic-1");
    }

    [Fact]
    public void StopResuming_RemovesTopicFromResumingTopics()
    {
        _dispatcher.Dispatch(new StartResuming("topic-1"));
        _dispatcher.Dispatch(new StopResuming("topic-1"));

        _store.State.ResumingTopics.ShouldNotContain("topic-1");
    }

    [Fact]
    public void MultipleTopics_StreamIndependently()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamStarted("topic-2"));

        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-2", "World", null, null, "msg-2"));

        _store.State.StreamingByTopic["topic-1"].Content.ShouldBe("Hello");
        _store.State.StreamingByTopic["topic-2"].Content.ShouldBe("World");

        _dispatcher.Dispatch(new StreamCompleted("topic-1"));

        _store.State.StreamingByTopic.ShouldNotContainKey("topic-1");
        _store.State.StreamingByTopic.ShouldContainKey("topic-2");
        _store.State.StreamingByTopic["topic-2"].Content.ShouldBe("World");
    }

    [Fact]
    public void StreamChunk_HandlesNullMessageIdGracefully()
    {
        _dispatcher.Dispatch(new StreamStarted("topic-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", "Hello", null, null, "msg-1"));
        _dispatcher.Dispatch(new StreamChunk("topic-1", " World", null, null, null));

        var content = _store.State.StreamingByTopic["topic-1"];
        content.CurrentMessageId.ShouldBe("msg-1"); // Preserved from previous
    }
}