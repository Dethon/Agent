using Infrastructure.Clients;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Message = Telegram.Bot.Types.Message;

namespace Tests.Unit.Clients;

public class TelegramBotChatClientTests
{
    private readonly Mock<ITelegramBotClient> _botClientMock;
    private readonly string[] _allowedUserNames = ["user1", "user2"];
    private readonly TelegramBotChatClient _chatClient;

    public TelegramBotChatClientTests()
    {
        _botClientMock = new Mock<ITelegramBotClient>();
        _chatClient = new TelegramBotChatClient(_botClientMock.Object, _allowedUserNames);
    }

    [Fact]
    public async Task SendResponse_WithValidMessage_ReturnsSentMessageId()
    {
        // given
        const long chatId = 123456789;
        const string response = "Test response";
        const int expectedMessageId = 42;

        _botClientMock.Setup(c => c.SendMessage(
                chatId,
                It.IsAny<string>(),
                It.IsAny<ParseMode>(),
                It.IsAny<ReplyParameters?>(),
                It.IsAny<ReplyMarkup?>(),
                It.IsAny<LinkPreviewOptions?>(),
                It.IsAny<int?>(),
                It.IsAny<IEnumerable<MessageEntity>?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message
            {
                Id = expectedMessageId
            });

        // when
        var result = await _chatClient.SendResponse(chatId, response);

        // then
        result.ShouldBe(expectedMessageId);
    }

    [Fact]
    public async Task SendResponse_WithLongMessage_TruncatesMessage()
    {
        // given
        const long chatId = 123456789;
        var longResponse = new string('A', 5000); // Create a string that exceeds the 4050 limit

        _botClientMock.Setup(c => c.SendMessage(
                chatId,
                It.IsAny<string>(),
                It.IsAny<ParseMode>(),
                It.IsAny<ReplyParameters?>(),
                It.IsAny<ReplyMarkup?>(),
                It.IsAny<LinkPreviewOptions?>(),
                It.IsAny<int?>(),
                It.IsAny<IEnumerable<MessageEntity>?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message
            {
                Id = 1
            });

        // when
        await _chatClient.SendResponse(chatId, longResponse);

        // then
        _botClientMock.Verify(c => c.SendMessage(
            chatId,
            It.Is<string>(s => s.Length <= 4050 && s.EndsWith("... (truncated)")),
            ParseMode.Html,
            null,
            false,
            false,
            0,
            false,
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendResponse_WithReplyId_PassesReplyParameters()
    {
        // given
        const long chatId = 123456789;
        const string response = "Test response";
        const int replyId = 7;

        _botClientMock.Setup(c => c.SendMessage(
                chatId,
                It.IsAny<string>(),
                It.IsAny<ParseMode>(),
                replyId,
                It.IsAny<ReplyMarkup?>(),
                It.IsAny<LinkPreviewOptions?>(),
                It.IsAny<int?>(),
                It.IsAny<IEnumerable<MessageEntity>?>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message
            {
                Id = 1
            });


        // when
        await _chatClient.SendResponse(chatId, response, replyId);

        // then
        _botClientMock.Verify(c => c.SendMessage(
            chatId,
            It.IsAny<string>(),
            ParseMode.Html,
            null,
            false,
            false,
            0,
            false,
            null,
            replyId,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}