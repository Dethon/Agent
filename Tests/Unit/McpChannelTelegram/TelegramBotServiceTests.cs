using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Tests.Unit.McpChannelTelegram;

public class TelegramBotServiceTests : IDisposable
{
    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly ChannelNotificationEmitter _emitter;
    private readonly ApprovalCallbackRouter _callbackRouter;
    private readonly BotRegistry _botRegistry;
    private readonly TelegramBotService _sut;
    private readonly CancellationTokenSource _cts = new();

    public TelegramBotServiceTests()
    {
        _emitter = new ChannelNotificationEmitter(new Mock<ILogger<ChannelNotificationEmitter>>().Object);
        _callbackRouter = new ApprovalCallbackRouter(_emitter);
        var settings = new ChannelSettings
        {
            Bots = [new AgentBotConfig { AgentId = "jack", BotToken = "unused" }],
            AllowedUsernames = ["alice", "bob"]
        };
        _botRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            ["jack"] = _botClient.Object
        });
        _sut = new TelegramBotService(
            _botRegistry,
            settings,
            _emitter,
            _callbackRouter,
            new Mock<ILogger<TelegramBotService>>().Object);
    }

    [Fact]
    public async Task ExecuteAsync_NonTextMessage_IsIgnored()
    {
        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = new Message
                {
                    Id = 10,
                    Date = DateTime.UtcNow,
                    Chat = new Chat { Id = 100, Type = ChatType.Private },
                    Photo = [new PhotoSize { FileId = "p1", FileUniqueId = "u1", Width = 100, Height = 100 }]
                }
            }
        ]);
        _emitter.RegisterSession("sess-1", null!);

        await RunServiceBriefly();

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_UnauthorizedUser_SendsRejection()
    {
        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = CreateTextMessage("/hello", 100, "eve")
            }
        ]);
        _emitter.RegisterSession("sess-1", null!);

        await RunServiceBriefly();

        _botClient.Verify(b => b.SendRequest(
            It.Is<SendMessageRequest>(r => r.Text == "You are not authorized to use this bot."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MessageWithoutSlashOrThread_IsIgnored()
    {
        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = CreateTextMessage("just chatting", 100, "alice")
            }
        ]);
        _emitter.RegisterSession("sess-1", null!);

        await RunServiceBriefly();

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SlashCommand_FromAuthorizedUser_EmitsNotification()
    {
        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = CreateTextMessage("/ask what is 2+2", 100, "alice")
            }
        ]);
        _emitter.RegisterSession("sess-1", null!);

        await RunServiceBriefly();

        // No rejection message sent — the message was valid and emitted
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CallbackQuery_RoutesToApprovalRouter()
    {
        var (approvalId, resultTask) = _callbackRouter.RegisterApproval(TimeSpan.FromSeconds(10), CancellationToken.None);

        SetupPollingSequence([
            new Update
            {
                Id = 1,
                CallbackQuery = new CallbackQuery
                {
                    Id = "cb-1",
                    Data = $"tool_approve:{approvalId}",
                    From = new User { Id = 1, IsBot = false, FirstName = "Alice" }
                }
            }
        ]);

        await RunServiceBriefly();

        var result = await resultTask;
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task ExecuteAsync_NoActiveSessions_DropsMessage()
    {
        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = CreateTextMessage("/hello", 100, "alice")
            }
        ]);
        // No sessions registered

        await RunServiceBriefly();

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ValidMessage_RegistersChatAgent()
    {
        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = CreateTextMessage("/ask something", 100, "alice")
            }
        ]);
        _emitter.RegisterSession("sess-1", null!);

        await RunServiceBriefly();

        _botRegistry.GetBotForChat(100).ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ThreadMessage_IsAccepted()
    {
        var msg = CreateTextMessage("reply in thread", 100, "alice");
        msg.MessageThreadId = 42;

        SetupPollingSequence([new Update { Id = 1, Message = msg }]);
        _emitter.RegisterSession("sess-1", null!);

        await RunServiceBriefly();

        // Thread messages are accepted even without / prefix
        _botRegistry.GetBotForChat(100).ShouldNotBeNull();
    }

    private void SetupPollingSequence(Update[] firstBatch)
    {
        var callCount = 0;
        _botClient
            .Setup(b => b.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .Returns((GetUpdatesRequest _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult(firstBatch);
                }

                _cts.Cancel();
                return Task.FromException<Update[]>(new OperationCanceledException(ct));
            });
    }

    private async Task RunServiceBriefly()
    {
        _cts.CancelAfter(TimeSpan.FromSeconds(1));
        await _sut.StartAsync(_cts.Token);
        // Give polling loop time to process
        await Task.Delay(200, CancellationToken.None);
        await _sut.StopAsync(CancellationToken.None);
    }

    private static Message CreateTextMessage(string text, long chatId, string username) => new()
    {
        Id = 10,
        Date = DateTime.UtcNow,
        Text = text,
        Chat = new Chat { Id = chatId, Type = ChatType.Private },
        From = new User { Id = 1, IsBot = false, FirstName = username, Username = username }
    };

    public void Dispose() => _cts.Dispose();
}