using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Infrastructure.Clients.ToolApproval;
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
    private const long TestChatId = 12345;
    private const int TestThreadId = 100;

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
    public async Task RequestApprovalAsync_WhenTimesOut_ReturnsRejected()
    {
        // Arrange
        var handler =
            new TelegramToolApprovalHandler(_botClient, TestChatId, TestThreadId, TimeSpan.FromMilliseconds(100));
        var requests = new List<ToolApprovalRequest>
        {
            new(null, "TestTool", new Dictionary<string, object?> { ["param"] = "value" })
        };

        SetupSendMessage(TestChatId);

        // Act
        var result = await handler.RequestApprovalAsync(requests, CancellationToken.None);

        // Assert
        result.ShouldBe(ToolApprovalResult.Rejected);
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_WithInvalidData_ReturnsFalse()
    {
        // Arrange
        var callbackQuery = CreateCallbackQueryWithoutMessage("invalid_data", TestChatId);

        // Act
        var handled = await TelegramToolApprovalHandler.HandleCallbackQueryAsync(
            _botClient, callbackQuery, CancellationToken.None);

        // Assert
        handled.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_WithExpiredApproval_AnswersWithExpiredMessage()
    {
        // Arrange
        SetupAnswerCallbackQuery();
        var callbackQuery = CreateCallbackQueryWithoutMessage("tool_approve:expired123", TestChatId);

        // Act
        var handled = await TelegramToolApprovalHandler.HandleCallbackQueryAsync(
            _botClient, callbackQuery, CancellationToken.None);

        // Assert
        handled.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleCallbackQueryAsync_WithEmptyData_ReturnsFalse()
    {
        // Arrange
        var callbackQuery = CreateCallbackQueryWithoutMessage(null, TestChatId);

        // Act
        var handled = await TelegramToolApprovalHandler.HandleCallbackQueryAsync(
            _botClient, callbackQuery, CancellationToken.None);

        // Assert
        handled.ShouldBeFalse();
    }

    [Fact]
    public void Factory_CreatesHandlerWithCorrectContext()
    {
        // Arrange
        const string agentId = "test-agent";
        var botsByAgentId = new Dictionary<string, ITelegramBotClient> { [agentId] = _botClient };
        var factory = new TelegramToolApprovalHandlerFactory(botsByAgentId);
        var agentKey = new AgentKey(TestChatId, TestThreadId, agentId);

        // Act
        var handler = factory.Create(agentKey);

        // Assert
        handler.ShouldNotBeNull();
        handler.ShouldBeOfType<TelegramToolApprovalHandler>();
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