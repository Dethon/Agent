using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Clients;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Types;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

public class TelegramToolApprovalHandlerTests : IAsyncLifetime
{
    private const string TestBotToken = "123456789:ABC-DEF1234ghIkl-zyx57W2v1u123ew11";
    private WireMockServer _server = null!;
    private ITelegramBotClient _botClient = null!;

    public Task InitializeAsync()
    {
        _server = WireMockServer.Start();
        SetupGetMe();
        var options = new TelegramBotClientOptions(TestBotToken, _server.Url!);
        _botClient = new TelegramBotClient(options);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RequestApprovalAsync_WhenNoChatSet_ReturnsFalse()
    {
        // Arrange
        var handler = new TelegramToolApprovalHandler(_botClient);
        var requests = new List<ToolApprovalRequest>
        {
            new("TestTool", new Dictionary<string, object?> { ["param"] = "value" })
        };

        // Act
        var result = await handler.RequestApprovalAsync(requests, CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task RequestApprovalAsync_WhenTimesOut_ReturnsFalse()
    {
        // Arrange
        var handler = new TelegramToolApprovalHandler(_botClient, TimeSpan.FromMilliseconds(100));
        handler.SetActiveChat(12345, null);

        var requests = new List<ToolApprovalRequest>
        {
            new("TestTool", new Dictionary<string, object?> { ["param"] = "value" })
        };

        SetupSendMessage(12345);

        // Act
        var result = await handler.RequestApprovalAsync(requests, CancellationToken.None);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_WithInvalidData_ReturnsFalse()
    {
        // Arrange
        var handler = new TelegramToolApprovalHandler(_botClient);
        var callbackQuery = CreateCallbackQueryWithoutMessage("invalid_data", 12345);

        // Act
        var handled = await handler.HandleCallbackQueryAsync(callbackQuery, CancellationToken.None);

        // Assert
        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_WithExpiredApproval_AnswersWithExpiredMessage()
    {
        // Arrange
        var handler = new TelegramToolApprovalHandler(_botClient);
        SetupAnswerCallbackQuery();

        var callbackQuery = CreateCallbackQueryWithoutMessage("tool_approve:expired123", 12345);

        // Act
        var handled = await handler.HandleCallbackQueryAsync(callbackQuery, CancellationToken.None);

        // Assert
        handled.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_WithEmptyData_ReturnsFalse()
    {
        // Arrange
        var handler = new TelegramToolApprovalHandler(_botClient);
        var callbackQuery = CreateCallbackQueryWithoutMessage(null, 12345);

        // Act
        var handled = await handler.HandleCallbackQueryAsync(callbackQuery, CancellationToken.None);

        // Assert
        handled.ShouldBeFalse();
    }

    [Fact]
    public void SetActiveChat_UpdatesChatContext()
    {
        // Arrange
        var handler = new TelegramToolApprovalHandler(_botClient);

        // Act & Assert - no exception
        handler.SetActiveChat(12345, 100);
        handler.SetActiveChat(67890, null);
    }

    private static CallbackQuery CreateCallbackQueryWithoutMessage(string? data, long userId)
    {
        return new CallbackQuery
        {
            Id = "test_callback_id",
            Data = data,
            From = new User { Id = userId, FirstName = "Test", IsBot = false }
        };
    }

    private void SetupGetMe()
    {
        var response = new
        {
            ok = true,
            result = new
            {
                id = 123456789,
                is_bot = true,
                first_name = "TestBot",
                username = "test_bot"
            }
        };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{TestBotToken}/getMe")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    private void SetupSendMessage(long chatId)
    {
        var response = new
        {
            ok = true,
            result = new
            {
                message_id = 1,
                from = new { id = 123456789, is_bot = true, first_name = "TestBot" },
                chat = new { id = chatId, type = "private" },
                date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                text = "Approval message"
            }
        };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{TestBotToken}/sendMessage")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }

    private void SetupAnswerCallbackQuery()
    {
        var response = new { ok = true, result = true };

        _server.Given(
                Request.Create()
                    .WithPath($"/bot{TestBotToken}/answerCallbackQuery")
                    .UsingAnyMethod())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(response)));
    }
}