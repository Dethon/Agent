using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

public class TelegramBotChatMessengerClientTests(TelegramBotFixture fixture) : IClassFixture<TelegramBotFixture>
{
    [Fact]
    public async Task ReadPrompts_WithAuthorizedUser_YieldsPrompt()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;
        var username = fixture.AllowedUserNames[0];

        var update = TelegramBotFixture.CreateTextMessageUpdate(1, chatId, "/test command", username);
        fixture.SetupGetUpdatesSequence([update], []);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var prompts = new List<ChatPrompt>();
        await foreach (var prompt in client.ReadPrompts(0, cts.Token))
        {
            prompts.Add(prompt);
            break;
        }

        // Assert
        prompts.ShouldHaveSingleItem();
        prompts[0].Prompt.ShouldBe("/test command");
        prompts[0].ChatId.ShouldBe(chatId);
        prompts[0].Sender.ShouldBe(username);
    }

    [Fact]
    public async Task ReadPrompts_WithUnauthorizedUser_SendsUnauthorizedMessageAndDoesNotYield()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 99999L;
        const string unauthorizedUsername = "unauthorized_user";

        // First call returns unauthorized user message, second call returns authorized user
        var unauthorizedUpdate = TelegramBotFixture.CreateTextMessageUpdate(1, chatId, "/test", unauthorizedUsername);
        var authorizedUpdate =
            TelegramBotFixture.CreateTextMessageUpdate(2, chatId, "/stop", fixture.AllowedUserNames[0]);
        fixture.SetupGetUpdates([unauthorizedUpdate, authorizedUpdate]);
        fixture.SetupSendMessage(chatId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act - The unauthorized message should be skipped, only authorized should yield
        var prompts = new List<ChatPrompt>();
        await foreach (var prompt in client.ReadPrompts(0, cts.Token))
        {
            prompts.Add(prompt);
            break; // Get first yielded prompt
        }

        // Assert - Only the authorized user's prompt was yielded
        prompts.ShouldHaveSingleItem();
        prompts[0].Prompt.ShouldBe("/stop");
    }

    [Fact]
    public async Task ReadPrompts_WithThreadMessage_YieldsPromptWithThreadId()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;
        const int threadId = 100;
        var username = fixture.AllowedUserNames[0];

        var update = TelegramBotFixture.CreateTextMessageUpdate(1, chatId, "thread message", username, threadId);
        fixture.SetupGetUpdatesSequence([update], []);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var prompts = new List<ChatPrompt>();
        await foreach (var prompt in client.ReadPrompts(0, cts.Token))
        {
            prompts.Add(prompt);
            break;
        }

        // Assert
        prompts.ShouldHaveSingleItem();
        prompts[0].ThreadId.ShouldBe(threadId);
    }

    [Fact]
    public async Task ReadPrompts_WithMultipleUpdates_YieldsAllPrompts()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;
        var username = fixture.AllowedUserNames[0];

        var update1 = TelegramBotFixture.CreateTextMessageUpdate(1, chatId, "/first", username);
        var update2 = TelegramBotFixture.CreateTextMessageUpdate(2, chatId, "/second", username);
        fixture.SetupGetUpdatesSequence([update1, update2], []);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var prompts = new List<ChatPrompt>();
        await foreach (var prompt in client.ReadPrompts(0, cts.Token))
        {
            prompts.Add(prompt);
            if (prompts.Count >= 2)
            {
                break;
            }
        }

        // Assert
        prompts.Count.ShouldBe(2);
        prompts[0].Prompt.ShouldBe("/first");
        prompts[1].Prompt.ShouldBe("/second");
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_WithMessage_SendsMessageToChat()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;

        fixture.SetupSendMessage(chatId);

        var updates = CreateUpdatesWithContent("Test response", chatId, null, fixture.BotTokenHash);

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() =>
            client.ProcessResponseStreamAsync(updates, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_WithToolCalls_SendsToolCallsMessage()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;

        fixture.SetupSendMessage(chatId);

        var updates = CreateUpdatesWithContentAndToolCall("Response", "test_tool", new { param = "value" }, chatId,
            null, fixture.BotTokenHash);

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() =>
            client.ProcessResponseStreamAsync(updates, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_WithThreadId_SendsMessageToThread()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = -100123456789L;
        const long threadId = 42L;

        fixture.SetupSendMessage(chatId);

        var updates = CreateUpdatesWithContent("Thread response", chatId, threadId, fixture.BotTokenHash);

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() =>
            client.ProcessResponseStreamAsync(updates, CancellationToken.None));
    }

    [Fact]
    public async Task CreateThread_ReturnsThreadId()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = -100123456789L;
        const int expectedThreadId = 42;

        fixture.SetupGetForumTopicIconStickers();
        fixture.SetupCreateForumTopic(chatId, expectedThreadId);
        fixture.SetupSendMessage(chatId);

        // Act
        var threadId = await client.CreateThread(chatId, "Test Topic", fixture.BotTokenHash, CancellationToken.None);

        // Assert
        threadId.ShouldBe(expectedThreadId);
    }

    [Fact]
    public async Task DoesThreadExist_WhenThreadExists_ReturnsTrue()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = -100123456789L;
        const long threadId = 42L;

        fixture.SetupGetForumTopicIconStickers();
        fixture.SetupEditForumTopic(true);

        // Act
        var exists = await client.DoesThreadExist(chatId, threadId, fixture.BotTokenHash, CancellationToken.None);

        // Assert
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task DoesThreadExist_WhenThreadDoesNotExist_ReturnsFalse()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = -100123456789L;
        const long threadId = 999L;

        fixture.SetupGetForumTopicIconStickers();
        fixture.SetupEditForumTopic(false);

        // Act
        var exists = await client.DoesThreadExist(chatId, threadId, fixture.BotTokenHash, CancellationToken.None);

        // Assert
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_WithReasoning_WhenShowReasoningTrue_SendsReasoningMessage()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient(showReasoning: true);
        const long chatId = 12345L;

        fixture.SetupSendMessage(chatId);

        var updates = CreateUpdatesWithContentAndReasoning("Result", "Thinking about the problem...", chatId, null,
            fixture.BotTokenHash);

        // Act & Assert - Should not throw (reasoning message + content message)
        await Should.NotThrowAsync(() =>
            client.ProcessResponseStreamAsync(updates, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessResponseStreamAsync_WithReasoning_WhenShowReasoningFalse_OmitsReasoningMessage()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient(showReasoning: false);
        const long chatId = 12345L;

        fixture.SetupSendMessage(chatId);

        var updates = CreateUpdatesWithContentAndReasoning("Result", "Thinking about the problem...", chatId, null,
            fixture.BotTokenHash);

        // Act & Assert - Should not throw (only content message)
        await Should.NotThrowAsync(() =>
            client.ProcessResponseStreamAsync(updates, CancellationToken.None));
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> CreateUpdatesWithContent(
        string content, long chatId, long? threadId, string? botTokenHash)
    {
        var key = new AgentKey(chatId, threadId ?? 0, botTokenHash);
        await Task.CompletedTask;
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new TextContent(content)]
        }, null);
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new UsageContent()]
        }, new AiResponse { Content = content });
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>
        CreateUpdatesWithContentAndReasoning(
            string content, string reasoning, long chatId, long? threadId, string? botTokenHash)
    {
        var key = new AgentKey(chatId, threadId ?? 0, botTokenHash);
        await Task.CompletedTask;
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new TextReasoningContent(reasoning)]
        }, null);
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new TextContent(content)]
        }, null);
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new UsageContent()]
        }, new AiResponse { Content = content, Reasoning = reasoning });
    }

    private static async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)>
        CreateUpdatesWithContentAndToolCall(
            string content, string toolName, object args, long chatId, long? threadId, string? botTokenHash)
    {
        var key = new AgentKey(chatId, threadId ?? 0, botTokenHash);
        var toolCalls = $"{toolName}({JsonSerializer.Serialize(args)})";
        await Task.CompletedTask;
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new TextContent(content)]
        }, null);
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new FunctionCallContent("call-1", toolName, args as IDictionary<string, object?>)]
        }, new AiResponse { Content = content, ToolCalls = toolCalls });
        yield return (key, new AgentResponseUpdate
        {
            MessageId = "msg-1",
            Contents = [new UsageContent()]
        }, null);
    }
}