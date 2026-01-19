using Shouldly;
using System.Reactive.Linq;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;

namespace Tests.Unit.WebChat.Client.State;

public class MessagesStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _store;

    public MessagesStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new MessagesStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void MessagesLoaded_PopulatesMessagesByTopic()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" }
        };

        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Assert
        _store.State.MessagesByTopic.TryGetValue("topic-1", out var topicMessages).ShouldBeTrue();
        topicMessages!.Count.ShouldBe(2);
        topicMessages[0].Content.ShouldBe("Hello");
        topicMessages[1].Content.ShouldBe("Hi there");
    }

    [Fact]
    public void MessagesLoaded_AddsToLoadedTopics()
    {
        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));

        // Assert
        _store.State.LoadedTopics.Contains("topic-1").ShouldBeTrue();
    }

    [Fact]
    public void AddMessage_AppendsToExistingMessages()
    {
        // Arrange
        var initialMessages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", initialMessages));

        // Act
        _dispatcher.Dispatch(new AddMessage("topic-1", new ChatMessageModel { Role = "assistant", Content = "Hi" }));

        // Assert
        var messages = _store.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
        messages[1].Role.ShouldBe("assistant");
        messages[1].Content.ShouldBe("Hi");
    }

    [Fact]
    public void AddMessage_CreatesListForNewTopic()
    {
        // Act
        _dispatcher.Dispatch(new AddMessage("new-topic", new ChatMessageModel { Role = "user", Content = "First message" }));

        // Assert
        _store.State.MessagesByTopic.TryGetValue("new-topic", out var messages).ShouldBeTrue();
        messages!.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("First message");
    }

    [Fact]
    public void RemoveLastMessage_RemovesLastMessage()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "First" },
            new() { Role = "assistant", Content = "Second" }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RemoveLastMessage("topic-1"));

        // Assert
        var remaining = _store.State.MessagesByTopic["topic-1"];
        remaining.Count.ShouldBe(1);
        remaining[0].Content.ShouldBe("First");
    }

    [Fact]
    public void RemoveLastMessage_NoopForEmptyTopic()
    {
        // Arrange
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        var stateBefore = _store.State;

        // Act
        _dispatcher.Dispatch(new RemoveLastMessage("topic-1"));

        // Assert - state should be unchanged (same reference for empty case)
        _store.State.MessagesByTopic["topic-1"].Count.ShouldBe(0);
    }

    [Fact]
    public void RemoveLastMessage_NoopForNonExistentTopic()
    {
        // Arrange
        var stateBefore = _store.State;

        // Act
        _dispatcher.Dispatch(new RemoveLastMessage("non-existent"));

        // Assert - no crash, state unchanged
        _store.State.MessagesByTopic.ContainsKey("non-existent").ShouldBeFalse();
    }

    [Fact]
    public void ClearMessages_RemovesTopicMessages()
    {
        // Arrange
        var messages = new List<ChatMessageModel> { new() { Role = "user", Content = "Test" } };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new ClearMessages("topic-1"));

        // Assert
        _store.State.MessagesByTopic["topic-1"].Count.ShouldBe(0);
    }

    [Fact]
    public void ClearMessages_RemovesFromLoadedTopics()
    {
        // Arrange
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _store.State.LoadedTopics.Contains("topic-1").ShouldBeTrue();

        // Act
        _dispatcher.Dispatch(new ClearMessages("topic-1"));

        // Assert
        _store.State.LoadedTopics.Contains("topic-1").ShouldBeFalse();
    }

    [Fact]
    public void LoadedTopics_TracksLoadedTopicIds()
    {
        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _dispatcher.Dispatch(new MessagesLoaded("topic-2", []));

        // Assert
        _store.State.LoadedTopics.Count.ShouldBe(2);
        _store.State.LoadedTopics.Contains("topic-1").ShouldBeTrue();
        _store.State.LoadedTopics.Contains("topic-2").ShouldBeTrue();
    }

    [Fact]
    public void DifferentTopics_HaveIndependentMessageLists()
    {
        // Arrange
        var topic1Messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Topic 1 message" }
        };
        var topic2Messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Topic 2 message" }
        };

        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", topic1Messages));
        _dispatcher.Dispatch(new MessagesLoaded("topic-2", topic2Messages));
        _dispatcher.Dispatch(new AddMessage("topic-1", new ChatMessageModel { Role = "assistant", Content = "Reply 1" }));

        // Assert
        _store.State.MessagesByTopic["topic-1"].Count.ShouldBe(2);
        _store.State.MessagesByTopic["topic-2"].Count.ShouldBe(1);
    }

    [Fact]
    public async Task StateObservable_EmitsOnDispatch()
    {
        // Arrange
        var emittedStates = new List<MessagesState>();
        using var subscription = _store.StateObservable.Subscribe(state => emittedStates.Add(state));

        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [new ChatMessageModel { Content = "Test" }]));
        _dispatcher.Dispatch(new AddMessage("topic-1", new ChatMessageModel { Content = "Another" }));

        // Allow observable to emit
        await Task.Delay(10);

        // Assert
        emittedStates.Count.ShouldBeGreaterThanOrEqualTo(3); // Initial + 2 dispatches
        emittedStates.Last().MessagesByTopic["topic-1"].Count.ShouldBe(2);
    }

    [Fact]
    public void StateObservable_ReplaysCurrentStateToNewSubscriber()
    {
        // Arrange
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [new ChatMessageModel { Content = "Test" }]));
        MessagesState? receivedState = null;

        // Act
        using var subscription = _store.StateObservable.Subscribe(state => receivedState = state);

        // Assert - subscriber immediately receives current state
        receivedState.ShouldNotBeNull();
        receivedState.MessagesByTopic.ContainsKey("topic-1").ShouldBeTrue();
    }

    [Fact]
    public void ImmutableUpdate_DoesNotMutateOriginalState()
    {
        // Arrange
        var initialMessages = new List<ChatMessageModel> { new() { Content = "Initial" } };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", initialMessages));
        var stateAfterLoad = _store.State;
        var messagesAfterLoad = stateAfterLoad.MessagesByTopic["topic-1"];

        // Act
        _dispatcher.Dispatch(new AddMessage("topic-1", new ChatMessageModel { Content = "Added" }));

        // Assert - original state unchanged, new state has new message
        messagesAfterLoad.Count.ShouldBe(1); // Original unchanged
        _store.State.MessagesByTopic["topic-1"].Count.ShouldBe(2); // New state has 2
        _store.State.ShouldNotBeSameAs(stateAfterLoad); // Different instances
    }
}
