using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;

namespace Tests.Unit.McpChannelTelegram;

public class RequestApprovalToolTests
{
    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly ApprovalCallbackRouter _router = new();
    private readonly IServiceProvider _services;

    public RequestApprovalToolTests()
    {
        var botRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            ["jack"] = _botClient.Object
        });
        botRegistry.RegisterChatAgent(100, "jack");

        _services = new ServiceCollection()
            .AddSingleton(botRegistry)
            .AddSingleton(_router)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task McpRun_NotifyMode_SendsAutoApprovedMessage()
    {
        const string requests = """[{"toolName":"mcp:server:search","arguments":{"q":"test"}}]""";

        var result = await RequestApprovalTool.McpRun("100:100", "notify", requests, _services);

        result.ShouldBe("notified");
        _botClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text.Contains("Auto-approved") && r.Text.Contains("search")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_NotifyMode_MultipleTools_ListsAllNames()
    {
        const string requests = """[{"toolName":"mcp:a:search","arguments":{}},{"toolName":"mcp:b:write","arguments":{}}]""";

        var result = await RequestApprovalTool.McpRun("100:100", "notify", requests, _services);

        result.ShouldBe("notified");
        _botClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text.Contains("search") && r.Text.Contains("write")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_RequestMode_SendsKeyboardAndWaitsForApproval()
    {
        const string requests = """[{"toolName":"mcp:server:delete","arguments":{"path":"/tmp/file"}}]""";

        var approvalTask = Task.Run(async () =>
            await RequestApprovalTool.McpRun("100:100", "request", requests, _services));

        // Give time for the approval to be registered
        await Task.Delay(200);

        // Verify keyboard message was sent
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // The tool is now waiting for approval — we won't resolve it in this test
        // (it would timeout after 2 min). Just verify the message was sent.
        approvalTask.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task McpRun_RequestMode_ApprovalGranted_ReturnsApproved()
    {
        const string requests = """[{"toolName":"tool","arguments":{}}]""";

        _ = Task.Run(async () =>
            await RequestApprovalTool.McpRun("100:100", "request", requests, _services));

        await Task.Delay(200);

        // Find the registered approval and approve it via callback
        var callbackQuery = new Telegram.Bot.Types.CallbackQuery
        {
            Id = "cb-1",
            From = new Telegram.Bot.Types.User { Id = 1, IsBot = false, FirstName = "Test" }
        };

        // We need to get the approvalId. Since it's generated internally,
        // we'll capture it from the sent message's inline keyboard.
        SendMessageRequest? capturedRequest = null;
        _botClient
            .Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Telegram.Bot.Requests.Abstractions.IRequest<Telegram.Bot.Types.Message>, CancellationToken>(
                (req, _) => capturedRequest = req as SendMessageRequest)
            .ReturnsAsync(new Telegram.Bot.Types.Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = new Telegram.Bot.Types.Chat { Id = 100, Type = Telegram.Bot.Types.Enums.ChatType.Private }
            });

        // Re-run with the setup
        var approvalTask2 = Task.Run(async () =>
            await RequestApprovalTool.McpRun("100:100", "request", requests, _services));

        await Task.Delay(300);

        // Extract approvalId from keyboard callback data
        if (capturedRequest?.ReplyMarkup is InlineKeyboardMarkup keyboard)
        {
            var approveButton = keyboard.InlineKeyboard.First().First();
            callbackQuery.Data = approveButton.CallbackData;
            await _router.HandleCallbackQueryAsync(_botClient.Object, callbackQuery, CancellationToken.None);
        }

        var result = await approvalTask2;
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task McpRun_UnknownChat_ThrowsInvalidOperation()
    {
        const string requests = """[{"toolName":"tool","arguments":{}}]""";

        await Should.ThrowAsync<InvalidOperationException>(
            () => RequestApprovalTool.McpRun("999:999", "notify", requests, _services));
    }

    [Fact]
    public async Task McpRun_NotifyMode_ExtractsShortToolName()
    {
        const string requests = """[{"toolName":"mcp:very:long:prefix:actual_tool","arguments":{}}]""";

        await RequestApprovalTool.McpRun("100:100", "notify", requests, _services);

        _botClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text.Contains("actual_tool")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}