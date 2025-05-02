using Infrastructure.Clients;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;
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
    public async Task SendResponse_WithLongMessage_TruncatesMessage()
    {
        // given
        const int expectedMessageId = 42;
        const long chatId = 123456789;
        var longResponse = new string('A', 5000);

        _botClientMock
            .Setup(c => c.SendRequest(
                It.Is<SendMessageRequest>(x => x.ChatId == chatId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message
            {
                Id = expectedMessageId
            });

        // when
        var result = await _chatClient.SendResponse(chatId, longResponse);

        // then
        result.ShouldBe(expectedMessageId);
        _botClientMock.Verify(c => c.SendRequest(
            It.Is<SendMessageRequest>(x => x.ChatId == chatId &&
                                           x.Text == $"{longResponse.Substring(0, 4050)} ... (truncated)" &&
                                           x.ParseMode == ParseMode.Html),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendResponse_WithReplyId_PassesReplyParameters()
    {
        // given
        const int expectedMessageId = 42;
        const long chatId = 123456789;
        const string response = "Test response";
        const int replyId = 7;

        _botClientMock
            .Setup(c => c.SendRequest(
                It.Is<SendMessageRequest>(x => x.ChatId == chatId &&
                                               x.Text == response &&
                                               x.ReplyParameters!.MessageId == replyId &&
                                               x.ParseMode == ParseMode.Html),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Message
            {
                Id = expectedMessageId
            });

        // when
        var result = await _chatClient.SendResponse(chatId, response, replyId);

        // then
        result.ShouldBe(expectedMessageId);
        _botClientMock.Verify(c => c.SendRequest(
            It.Is<SendMessageRequest>(x => x.ChatId == chatId &&
                                           x.Text == response &&
                                           x.ReplyParameters!.MessageId == replyId &&
                                           x.ParseMode == ParseMode.Html),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}