using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Messaging;

public class CompositeChatMessengerClientTests
{
    [Fact]
    public void SupportsScheduledNotifications_WhenAnyClientSupports_ReturnsTrue()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.SupportsScheduledNotifications).Returns(true);
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act & Assert
        composite.SupportsScheduledNotifications.ShouldBeTrue();
    }

    [Fact]
    public void SupportsScheduledNotifications_WhenNoClientSupports_ReturnsFalse()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.SupportsScheduledNotifications).Returns(false);
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act & Assert
        composite.SupportsScheduledNotifications.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadPrompts_MergesPromptsFromAllClients()
    {
        // Arrange
        var prompt1 = new ChatPrompt
        {
            Prompt = "From client 1",
            ChatId = 1,
            ThreadId = 1,
            MessageId = 1,
            Sender = "user1",
            Source = MessageSource.WebUi
        };

        var prompt2 = new ChatPrompt
        {
            Prompt = "From client 2",
            ChatId = 2,
            ThreadId = 2,
            MessageId = 2,
            Sender = "user2",
            Source = MessageSource.WebUi
        };

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { prompt1 }.ToAsyncEnumerable());

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client2.Setup(c => c.ReadPrompts(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { prompt2 }.ToAsyncEnumerable());

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var prompts = new List<ChatPrompt>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await foreach (var prompt in composite.ReadPrompts(100, cts.Token))
            {
                prompts.Add(prompt);
                if (prompts.Count >= 2)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }

        // Assert
        prompts.Count.ShouldBe(2);
        prompts.ShouldContain(p => p.Prompt == "From client 1");
        prompts.ShouldContain(p => p.Prompt == "From client 2");
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_BroadcastsToWebUiClients()
    {
        // Arrange
        var receivedUpdates1 = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();
        var receivedUpdates2 = new List<(AgentKey, AgentResponseUpdate, AiResponse?)>();

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
                CancellationToken ct) =>
            {
                await foreach (var update in updates.WithCancellation(ct))
                {
                    receivedUpdates1.Add(update);
                }
            });

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client2.Setup(c => c.ProcessResponseStreamAsync(
                It.IsAny<IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
                CancellationToken ct) =>
            {
                await foreach (var update in updates.WithCancellation(ct))
                {
                    receivedUpdates2.Add(update);
                }
            });

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        var testUpdate = (new AgentKey(1, 1, "agent"),
            new AgentResponseUpdate { Contents = [new TextContent("Hello")] },
            (AiResponse?)null);

        // Act
        await composite.ProcessResponseStreamAsync(
            new[] { testUpdate }.ToAsyncEnumerable(),
            CancellationToken.None);

        // Assert
        receivedUpdates1.Count.ShouldBe(1);
        receivedUpdates2.Count.ShouldBe(1);
        receivedUpdates1[0].Item2.Contents.OfType<TextContent>().ShouldContain(tc => tc.Text == "Hello");
        receivedUpdates2[0].Item2.Contents.OfType<TextContent>().ShouldContain(tc => tc.Text == "Hello");
    }

    [Fact]
    public async Task DoesThreadExist_ReturnsTrueIfAnyClientReturnsTrue()
    {
        // Arrange
        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client2.Setup(c => c.DoesThreadExist(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var result = await composite.DoesThreadExist(123, 456, "agent1", CancellationToken.None);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateTopicIfNeededAsync_DelegatesToFirstClient()
    {
        // Arrange
        var expectedKey = new AgentKey(123, 456, "agent1");

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.CreateTopicIfNeededAsync(It.IsAny<long?>(), It.IsAny<long?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedKey);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        var result = await composite.CreateTopicIfNeededAsync(123, 456, "agent1", "topic");

        // Assert
        result.ShouldBe(expectedKey);
        client1.Verify(c => c.CreateTopicIfNeededAsync(123, 456, "agent1", "topic",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartScheduledStreamAsync_DelegatesToAllClients()
    {
        // Arrange
        var agentKey = new AgentKey(1, 1, "agent");

        var client1 = new Mock<IChatMessengerClient>();
        client1.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client1.Setup(c => c.StartScheduledStreamAsync(It.IsAny<AgentKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var client2 = new Mock<IChatMessengerClient>();
        client2.Setup(c => c.Source).Returns(MessageSource.WebUi);
        client2.Setup(c => c.StartScheduledStreamAsync(It.IsAny<AgentKey>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object]);

        // Act
        await composite.StartScheduledStreamAsync(agentKey);

        // Assert
        client1.Verify(c => c.StartScheduledStreamAsync(agentKey, It.IsAny<CancellationToken>()), Times.Once);
        client2.Verify(c => c.StartScheduledStreamAsync(agentKey, It.IsAny<CancellationToken>()), Times.Once);
    }
}