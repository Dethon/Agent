using Domain.DTOs;
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
        prompts[0].Prompt.ShouldBe("test command");
        prompts[0].ChatId.ShouldBe(chatId);
        prompts[0].IsCommand.ShouldBeTrue();
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
        prompts[0].Prompt.ShouldBe("stop");
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
        prompts[0].IsCommand.ShouldBeFalse();
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
            if (prompts.Count >= 2) break;
        }

        // Assert
        prompts.Count.ShouldBe(2);
        prompts[0].Prompt.ShouldBe("first");
        prompts[1].Prompt.ShouldBe("second");
    }

    [Fact]
    public async Task SendResponse_WithMessage_SendsMessageToChat()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;

        fixture.SetupSendMessage(chatId);

        var responseMessage = new ChatResponseMessage
        {
            Message = "Test response",
            Bold = false
        };

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() =>
            client.SendResponse(chatId, responseMessage, null, CancellationToken.None));
    }

    [Fact]
    public async Task SendResponse_WithBoldMessage_SendsBoldFormattedMessage()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;

        fixture.SetupSendMessage(chatId);

        var responseMessage = new ChatResponseMessage
        {
            Message = "Bold response",
            Bold = true
        };

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() =>
            client.SendResponse(chatId, responseMessage, null, CancellationToken.None));
    }

    [Fact]
    public async Task SendResponse_WithToolCalls_SendsToolCallsMessage()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;

        fixture.SetupSendMessage(chatId);

        var responseMessage = new ChatResponseMessage
        {
            Message = "Response with tools",
            CalledTools = """{"tool": "test", "args": {"param": "value"}}"""
        };

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() =>
            client.SendResponse(chatId, responseMessage, null, CancellationToken.None));
    }

    [Fact]
    public async Task SendResponse_WithThreadId_SendsMessageToThread()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = -100123456789L;
        const long threadId = 42L;

        fixture.SetupSendMessage(chatId);

        var responseMessage = new ChatResponseMessage
        {
            Message = "Thread response"
        };

        // Act & Assert - Should not throw
        await Should.NotThrowAsync(() =>
            client.SendResponse(chatId, responseMessage, threadId, CancellationToken.None));
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

        // Act
        var threadId = await client.CreateThread(chatId, "Test Topic", CancellationToken.None);

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
        var exists = await client.DoesThreadExist(chatId, threadId, CancellationToken.None);

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
        var exists = await client.DoesThreadExist(chatId, threadId, CancellationToken.None);

        // Assert
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task BlockWhile_ExecutesTask()
    {
        // Arrange
        fixture.Reset();
        var client = fixture.CreateClient();
        const long chatId = 12345L;
        var taskExecuted = false;

        // Act
        await client.BlockWhile(chatId, null, () =>
        {
            taskExecuted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        // Assert
        taskExecuted.ShouldBeTrue();
    }
}