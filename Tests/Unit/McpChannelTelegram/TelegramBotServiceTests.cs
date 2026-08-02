using Channels.Hosting;
using Domain.Channels;
using Domain.DTOs.Channel;
using McpChannelTelegram.Services;
using McpChannelTelegram.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
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
    private readonly FakeTimeProvider _time = new();
    private readonly ChannelInbox _inbox;
    private readonly ChannelNotificationEmitter _emitter;
    private readonly ApprovalCallbackRouter _callbackRouter = new();
    private readonly BotRegistry _botRegistry;
    private readonly TelegramBotService _sut;
    private readonly CancellationTokenSource _cts = new();

    public TelegramBotServiceTests()
    {
        _inbox = new ChannelInbox(_time);
        _emitter = new ChannelNotificationEmitter(
            _inbox, DeliveryPolicy.BufferAlways, ChannelProtocol.ChannelClientNamePrefix + "telegram");
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
        await _inbox.ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None);

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
        await _inbox.ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None);

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
        await _inbox.ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None);

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
        await _inbox.ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None);

        await RunServiceBriefly();

        // No rejection message sent — the message was valid and emitted
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        (await _inbox.ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
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

    // No subscriber is registered at all here — the cold-start case. Two things are pinned:
    // Telegram stays quiet toward the sender (the reverted drop policy is not allowed to grow
    // back a "the agent is unavailable" reply), and the message is buffered anyway, because the
    // emitter targets the well-known subscriber id and mints its queue on demand.
    [Fact]
    public async Task ExecuteAsync_NoActiveSessions_BuffersSilentlyWithoutRejectingTheSender()
    {
        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = CreateTextMessage("/hello", 100, "alice")
            }
        ]);

        await RunServiceBriefly();

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);

        var buffered = await _inbox.ReceiveAsync(
            ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None);
        buffered.ShouldHaveSingleItem().Message!.Content.ShouldBe("/hello");
    }

    // Corrects a regression this suite itself introduced: an earlier round made Telegram gate
    // EmitMessageNotificationAsync on HasActiveSessions, so a stale (but not yet evicted)
    // subscriber caused an unconditional drop with only a log line — silent loss to a user actively
    // waiting for a reply. Before that, the same scenario buffered the message and delivered it
    // late on the agent's next reconnect poll (the stable "channel-telegram" subscriber id survives
    // the disconnect). Telegram's own emit path has no way to signal failure back to the sender
    // (unlike ServiceBus's broker-level abandon/redeliver, or Schedule/Library's durable record),
    // so buffering — not dropping — is the correct behavior here: the message must always reach the
    // inbox, regardless of whether anyone is known to be listening right now.
    [Fact]
    public async Task ExecuteAsync_SubscriberWentStaleWithoutRepolling_StillBuffersForALaterPoll()
    {
        var subscriberId = ChannelProtocol.ChannelClientNamePrefix + "telegram";
        await _inbox.ReceiveAsync(subscriberId, TimeSpan.Zero, CancellationToken.None);
        _time.Advance(ChannelProtocol.LiveSubscriberFreshness + TimeSpan.FromSeconds(1));

        SetupPollingSequence([
            new Update
            {
                Id = 1,
                Message = CreateTextMessage("/ask what is 2+2", 100, "alice")
            }
        ]);

        await RunServiceBriefly();

        var batch = await _inbox.ReceiveAsync(subscriberId, TimeSpan.Zero, CancellationToken.None);
        batch.Count.ShouldBe(1);
        batch[0].Message!.Content.ShouldBe("/ask what is 2+2");
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
        await _inbox.ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None);

        await RunServiceBriefly();

        _botRegistry.GetBotForChat(100).ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ThreadMessage_IsAccepted()
    {
        var msg = CreateTextMessage("reply in thread", 100, "alice");
        msg.MessageThreadId = 42;

        SetupPollingSequence([new Update { Id = 1, Message = msg }]);
        await _inbox.ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "telegram", TimeSpan.Zero, CancellationToken.None);

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