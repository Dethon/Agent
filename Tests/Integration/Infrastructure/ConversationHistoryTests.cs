using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Integration.Infrastructure;

public class ConversationHistoryTests
{
    [Fact]
    public void Constructor_WithInitialMessages_ContainsMessages()
    {
        // Arrange
        var initialMessages = new[]
        {
            new ChatMessage(ChatRole.System, "You are a helpful assistant."),
            new ChatMessage(ChatRole.User, "Hello")
        };

        // Act
        var history = new ConversationHistory(initialMessages);
        var snapshot = history.GetSnapshot();

        // Assert
        snapshot.Count.ShouldBe(2);
        snapshot[0].Role.ShouldBe(ChatRole.System);
        snapshot[1].Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public void AddMessages_FromChatMessages_AppendsMessages()
    {
        // Arrange
        var history = new ConversationHistory([]);
        var newMessages = new[]
        {
            new ChatMessage(ChatRole.User, "Hello"), new ChatMessage(ChatRole.Assistant, "Hi there!")
        };

        // Act
        history.AddMessages(newMessages);
        var snapshot = history.GetSnapshot();

        // Assert
        snapshot.Count.ShouldBe(2);
        snapshot[0].Text.ShouldBe("Hello");
        snapshot[1].Text.ShouldBe("Hi there!");
    }

    [Fact]
    public void AddOrChangeSystemPrompt_WhenNoSystemMessage_InsertsAtStart()
    {
        // Arrange
        var history = new ConversationHistory([
            new ChatMessage(ChatRole.User, "Hello")
        ]);

        // Act
        history.AddOrChangeSystemPrompt("New system prompt");
        var snapshot = history.GetSnapshot();

        // Assert
        snapshot.Count.ShouldBe(2);
        snapshot[0].Role.ShouldBe(ChatRole.System);
        snapshot[0].Text.ShouldBe("New system prompt");
        snapshot[1].Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public void AddOrChangeSystemPrompt_WhenExists_UpdatesContent()
    {
        // Arrange
        var history = new ConversationHistory([
            new ChatMessage(ChatRole.System, "Original system prompt"),
            new ChatMessage(ChatRole.User, "Hello")
        ]);

        // Act
        history.AddOrChangeSystemPrompt("Updated system prompt");
        var snapshot = history.GetSnapshot();

        // Assert
        snapshot.Count.ShouldBe(2);
        snapshot[0].Role.ShouldBe(ChatRole.System);
        snapshot[0].Text.ShouldBe("Updated system prompt");
    }

    [Fact]
    public void AddOrChangeSystemPrompt_WithNull_DoesNothing()
    {
        // Arrange
        var history = new ConversationHistory([
            new ChatMessage(ChatRole.User, "Hello")
        ]);

        // Act
        history.AddOrChangeSystemPrompt(null);
        var snapshot = history.GetSnapshot();

        // Assert
        snapshot.Count.ShouldBe(1);
        snapshot[0].Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public void GetSnapshot_ReturnsImmutableCopy()
    {
        // Arrange
        var history = new ConversationHistory([
            new ChatMessage(ChatRole.User, "Hello")
        ]);

        // Act
        var snapshot1 = history.GetSnapshot();
        history.AddMessages([new ChatMessage(ChatRole.Assistant, "Hi!")]);
        var snapshot2 = history.GetSnapshot();

        // Assert
        snapshot1.Count.ShouldBe(1);
        snapshot2.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetSnapshot_IsThreadSafe()
    {
        // Arrange
        var history = new ConversationHistory([]);
        var exceptions = new List<Exception>();
        var tasks = new List<Task>();

        // Act - run concurrent reads and writes
        for (var i = 0; i < 100; i++)
        {
            var index = i;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    if (index % 2 == 0)
                    {
                        history.AddMessages([new ChatMessage(ChatRole.User, $"Message {index}")]);
                    }
                    else
                    {
                        _ = history.GetSnapshot();
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        exceptions.ShouldBeEmpty();
    }

    [Fact]
    public void AddMessages_WithEmptyEnumerable_DoesNothing()
    {
        // Arrange
        var history = new ConversationHistory([
            new ChatMessage(ChatRole.User, "Hello")
        ]);

        // Act
        history.AddMessages(Array.Empty<ChatMessage>());
        var snapshot = history.GetSnapshot();

        // Assert
        snapshot.Count.ShouldBe(1);
    }

    [Fact]
    public void AddMessages_PreservesMessageOrder()
    {
        // Arrange
        var history = new ConversationHistory([]);

        // Act
        history.AddMessages([new ChatMessage(ChatRole.User, "First")]);
        history.AddMessages([new ChatMessage(ChatRole.Assistant, "Second")]);
        history.AddMessages([new ChatMessage(ChatRole.User, "Third")]);

        var snapshot = history.GetSnapshot();

        // Assert
        snapshot.Count.ShouldBe(3);
        snapshot[0].Text.ShouldBe("First");
        snapshot[1].Text.ShouldBe("Second");
        snapshot[2].Text.ShouldBe("Third");
    }
}