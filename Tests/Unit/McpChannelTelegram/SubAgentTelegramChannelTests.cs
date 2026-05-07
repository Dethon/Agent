using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Tests.Unit.McpChannelTelegram;

public class SubAgentTelegramChannelTests
{
    private const long ChatId = 100L;
    private const string ConversationId = "100:100";
    private const string Handle = "worker-abc";
    private const string SubAgentId = "researcher";

    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly SubAgentCardStore _cardStore = new();
    private readonly Mock<ISubAgentCancelNotifier> _cancelNotifier = new();
    private readonly ApprovalCallbackRouter _callbackRouter;
    private readonly BotRegistry _botRegistry;
    private readonly IServiceProvider _services;

    public SubAgentTelegramChannelTests()
    {
        _callbackRouter = new ApprovalCallbackRouter(_cancelNotifier.Object);
        _botRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            ["jack"] = _botClient.Object
        });
        _botRegistry.RegisterChatAgent(ChatId, "jack");

        _services = new ServiceCollection()
            .AddSingleton(_botRegistry)
            .AddSingleton<ISubAgentCardStore>(_cardStore)
            .BuildServiceProvider();

        _botClient
            .Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message
            {
                Id = 42,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = ChatId, Type = ChatType.Private }
            });
    }

    [Fact]
    public async Task AnnounceTool_SendsMessageWithCancelKeyboard()
    {
        SendMessageRequest? captured = null;
        _botClient
            .Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Telegram.Bot.Requests.Abstractions.IRequest<Message>, CancellationToken>(
                (req, _) => captured = req as SendMessageRequest)
            .ReturnsAsync(new Message
            {
                Id = 42,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = ChatId, Type = ChatType.Private }
            });

        var result = await SubAgentAnnounceTool.McpRun(ConversationId, Handle, SubAgentId, _services);

        result.ShouldBe("announced");
        captured.ShouldNotBeNull();
        captured!.ParseMode.ShouldBe(ParseMode.Html);
        captured.Text.ShouldContain(SubAgentId);
        captured.Text.ShouldContain("Running");

        var keyboard = captured.ReplyMarkup as InlineKeyboardMarkup;
        keyboard.ShouldNotBeNull();
        var button = keyboard!.InlineKeyboard.First().First();
        button.CallbackData.ShouldBe($"subagent_cancel:{Handle}");
    }

    [Fact]
    public async Task AnnounceTool_TracksCardInStore()
    {
        await SubAgentAnnounceTool.McpRun(ConversationId, Handle, SubAgentId, _services);

        _cardStore.TryGet(Handle, out var card).ShouldBeTrue();
        card.ChatId.ShouldBe(ChatId);
        card.MessageId.ShouldBe(42);
        card.SubAgentId.ShouldBe(SubAgentId);
    }

    [Fact]
    public async Task AnnounceTool_HtmlEncodesSubAgentId()
    {
        SendMessageRequest? captured = null;
        _botClient
            .Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Telegram.Bot.Requests.Abstractions.IRequest<Message>, CancellationToken>(
                (req, _) => captured = req as SendMessageRequest)
            .ReturnsAsync(new Message
            {
                Id = 42,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = ChatId, Type = ChatType.Private }
            });

        await SubAgentAnnounceTool.McpRun(ConversationId, Handle, "R&D<script>", _services);

        captured.ShouldNotBeNull();
        captured!.Text.ShouldContain("R&amp;D&lt;script&gt;");
        captured.Text.ShouldNotContain("<script>");
    }

    [Fact]
    public async Task UpdateTool_TerminalStatus_EditsBothTextAndMarkup_RemovesCard()
    {
        _cardStore.Track(Handle, ChatId, 42, SubAgentId);

        var result = await SubAgentUpdateTool.McpRun(ConversationId, Handle, "completed", _services);

        result.ShouldBe("updated");

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<EditMessageTextRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _botClient.Verify(b => b.SendRequest(
            It.Is<EditMessageReplyMarkupRequest>(r => r.ReplyMarkup == null),
            It.IsAny<CancellationToken>()), Times.Once);

        _cardStore.TryGet(Handle, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task UpdateTool_RunningStatus_UpdatesTextOnly_KeepsCard()
    {
        _cardStore.Track(Handle, ChatId, 42, SubAgentId);

        var result = await SubAgentUpdateTool.McpRun(ConversationId, Handle, "running", _services);

        result.ShouldBe("updated");

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<EditMessageTextRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _botClient.Verify(b => b.SendRequest(
            It.IsAny<EditMessageReplyMarkupRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _cardStore.TryGet(Handle, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task UpdateTool_UnknownHandle_ReturnsNotFound()
    {
        var result = await SubAgentUpdateTool.McpRun(ConversationId, "no-such-handle", "completed", _services);

        result.ShouldBe("not_found");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<EditMessageTextRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CallbackQuery_SubagentCancel_EmitsNotificationAndAcknowledges()
    {
        var query = new CallbackQuery
        {
            Id = "cb-cancel-1",
            Data = $"subagent_cancel:{Handle}",
            From = new User { Id = 1, IsBot = false, FirstName = "Alice" },
            Message = new Message
            {
                Id = 42,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = ChatId, Type = ChatType.Private }
            }
        };

        var handled = await _callbackRouter.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        handled.ShouldBeTrue();
        _cancelNotifier.Verify(n => n.EmitCancelSubAgentNotificationAsync(
            "100:100",
            Handle,
            It.IsAny<CancellationToken>()), Times.Once);
        _botClient.Verify(b => b.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text == "Cancellation requested."),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CallbackQuery_SubagentCancel_WithThread_BuildsCorrectConversationId()
    {
        var query = new CallbackQuery
        {
            Id = "cb-cancel-2",
            Data = $"subagent_cancel:{Handle}",
            From = new User { Id = 1, IsBot = false, FirstName = "Alice" },
            Message = new Message
            {
                Id = 99,
                Date = DateTime.UtcNow,
                Chat = new Chat { Id = ChatId, Type = ChatType.Group },
                MessageThreadId = 55
            }
        };

        var handled = await _callbackRouter.HandleCallbackQueryAsync(_botClient.Object, query, CancellationToken.None);

        handled.ShouldBeTrue();
        _cancelNotifier.Verify(n => n.EmitCancelSubAgentNotificationAsync(
            "100:55",
            Handle,
            It.IsAny<CancellationToken>()), Times.Once);
        _botClient.Verify(b => b.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text == "Cancellation requested."),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
