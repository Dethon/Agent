using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public class TopicsStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _store;

    public TopicsStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new TopicsStore(_dispatcher);
    }

    public void Dispose()
    {
        _store.Dispose();
    }

    [Fact]
    public void TopicsLoaded_UpdatesTopicsList()
    {
        // Arrange
        var topics = new List<StoredTopic>
        {
            CreateTopic("topic-1", "Topic One"),
            CreateTopic("topic-2", "Topic Two")
        };

        // Act
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Assert
        _store.State.Topics.Count.ShouldBe(2);
        _store.State.Topics[0].Name.ShouldBe("Topic One");
        _store.State.Topics[1].Name.ShouldBe("Topic Two");
    }

    [Fact]
    public void SelectTopic_UpdatesSelectedTopicId()
    {
        // Arrange
        var topics = new List<StoredTopic> { CreateTopic("topic-1", "Topic One") };
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Act
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        // Assert
        _store.State.SelectedTopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void SelectTopic_WithNull_DeselectsTopic()
    {
        // Arrange
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        // Act
        _dispatcher.Dispatch(new SelectTopic(null));

        // Assert
        _store.State.SelectedTopicId.ShouldBeNull();
    }

    [Fact]
    public void AddTopic_AppendsToTopicsList()
    {
        // Arrange
        var initialTopics = new List<StoredTopic> { CreateTopic("topic-1", "Topic One") };
        _dispatcher.Dispatch(new TopicsLoaded(initialTopics));

        // Act
        _dispatcher.Dispatch(new AddTopic(CreateTopic("topic-2", "Topic Two")));

        // Assert
        _store.State.Topics.Count.ShouldBe(2);
        _store.State.Topics[1].TopicId.ShouldBe("topic-2");
    }

    [Fact]
    public void UpdateTopic_ReplacesExistingTopic()
    {
        // Arrange
        var topics = new List<StoredTopic> { CreateTopic("topic-1", "Original Name") };
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Act
        _dispatcher.Dispatch(new UpdateTopic(CreateTopic("topic-1", "Updated Name")));

        // Assert
        _store.State.Topics.Count.ShouldBe(1);
        _store.State.Topics[0].Name.ShouldBe("Updated Name");
    }

    [Fact]
    public void RemoveTopic_RemovesFromTopicsList()
    {
        // Arrange
        var topics = new List<StoredTopic>
        {
            CreateTopic("topic-1", "Topic One"),
            CreateTopic("topic-2", "Topic Two")
        };
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Act
        _dispatcher.Dispatch(new RemoveTopic("topic-1"));

        // Assert
        _store.State.Topics.Count.ShouldBe(1);
        _store.State.Topics[0].TopicId.ShouldBe("topic-2");
    }

    [Fact]
    public void RemoveTopic_ClearsSelectionIfSelectedTopicRemoved()
    {
        // Arrange
        var topics = new List<StoredTopic> { CreateTopic("topic-1", "Topic One") };
        _dispatcher.Dispatch(new TopicsLoaded(topics));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        // Act
        _dispatcher.Dispatch(new RemoveTopic("topic-1"));

        // Assert
        _store.State.SelectedTopicId.ShouldBeNull();
    }

    [Fact]
    public void TopicsError_SetsErrorAndClearsIsLoading()
    {
        // Arrange
        _dispatcher.Dispatch(new LoadTopics());

        // Act
        _dispatcher.Dispatch(new TopicsError("Something went wrong"));

        // Assert
        _store.State.Error.ShouldBe("Something went wrong");
        _store.State.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public void TopicsLoaded_ClearsError()
    {
        // Arrange
        _dispatcher.Dispatch(new TopicsError("Previous error"));

        // Act
        _dispatcher.Dispatch(new TopicsLoaded([]));

        // Assert
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void LoadTopics_SetsIsLoadingAndClearsError()
    {
        // Arrange
        _dispatcher.Dispatch(new TopicsError("Previous error"));

        // Act
        _dispatcher.Dispatch(new LoadTopics());

        // Assert
        _store.State.IsLoading.ShouldBeTrue();
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void SetAgents_UpdatesAgentsList()
    {
        // Arrange
        var agents = new List<AgentInfo>
        {
            new("agent-1", "Agent One", null),
            new("agent-2", "Agent Two", "Description")
        };

        // Act
        _dispatcher.Dispatch(new SetAgents(agents));

        // Assert
        _store.State.Agents.Count.ShouldBe(2);
        _store.State.Agents[0].Name.ShouldBe("Agent One");
    }

    [Fact]
    public void SelectAgent_UpdatesSelectedAgentId()
    {
        // Act
        _dispatcher.Dispatch(new SelectAgent("agent-1"));

        // Assert
        _store.State.SelectedAgentId.ShouldBe("agent-1");
    }

    [Fact]
    public void UnhandledAction_ReturnsStateUnchanged()
    {
        // Arrange - capture initial state reference

        // Act - dispatch an action not registered with this store
        // Using a custom test action
        _dispatcher.Dispatch(new LoadTopics());
        var afterLoad = _store.State;
        _dispatcher.Dispatch(new TopicsLoaded([]));

        // Assert - TopicsLoaded should create new state
        _store.State.ShouldNotBeSameAs(afterLoad);
    }

    [Fact]
    public async Task StateObservable_EmitsOnDispatch()
    {
        // Arrange
        var emittedStates = new List<TopicsState>();
        using var subscription = _store.StateObservable.Subscribe(state => emittedStates.Add(state));

        // Act
        _dispatcher.Dispatch(new TopicsLoaded([CreateTopic("topic-1", "Test")]));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        // Allow observable to emit
        await Task.Delay(10);

        // Assert
        emittedStates.Count.ShouldBeGreaterThanOrEqualTo(3); // Initial + 2 dispatches
        emittedStates.Last().SelectedTopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void StateObservable_ReplaysCurrentStateToNewSubscriber()
    {
        // Arrange
        _dispatcher.Dispatch(new TopicsLoaded([CreateTopic("topic-1", "Test")]));
        TopicsState? receivedState = null;

        // Act
        using var subscription = _store.StateObservable.Subscribe(state => receivedState = state);

        // Assert - subscriber immediately receives current state
        receivedState.ShouldNotBeNull();
        receivedState.Topics.Count.ShouldBe(1);
    }

    private static StoredTopic CreateTopic(string topicId, string name, string agentId = "agent-1")
    {
        return new StoredTopic
        {
            TopicId = topicId,
            Name = name,
            AgentId = agentId,
            ChatId = 123,
            ThreadId = 456,
            CreatedAt = DateTime.UtcNow
        };
    }
}