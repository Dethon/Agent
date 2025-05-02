using Domain.DTOs;
using Infrastructure.Clients;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
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
    public async Task ReadPrompts_WithAllowedUser_YieldsPrompts()
    {
        // given
        const long chatId = 123456789;
        const int messageId = 100;
        const int updateId = 1;
        const string messageText = "Hello bot";
        const string username = "user1";

        var update = new Update
        {
            Id = updateId,
            Message = new Message
            {
                Text = messageText,
                Chat = new Chat
                {
                    Id = chatId,
                    Username = username,
                    FirstName = "Test User"
                },
                Id = messageId
            }
        };

        _botClientMock
            .SetupSequence(c => c.SendRequest(
                It.IsAny<GetUpdatesRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .Throws(() => new OperationCanceledException());

        // when
        var cts = new CancellationTokenSource();
        var prompts = new List<ChatPrompt>();

        try
        {
            await foreach (var prompt in _chatClient.ReadPrompts(30, cts.Token))
            {
                prompts.Add(prompt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected exception to break out of the infinite loop
        }

        // then
        prompts.Count.ShouldBe(1);
        prompts[0].Prompt.ShouldBe(messageText);
        prompts[0].ChatId.ShouldBe(chatId);
        prompts[0].MessageId.ShouldBe(messageId);
        prompts[0].Sender.ShouldBe(username);
        prompts[0].ReplyToMessageId.ShouldBeNull();

        _botClientMock.Verify(c => c.SendRequest(
            It.Is<GetUpdatesRequest>(x =>
                x.Offset == null &&
                x.Timeout == 30),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadPrompts_WithUnauthorizedUser_SendsErrorMessage()
    {
        // given
        const long chatId = 123456789;
        const int messageId = 200;
        const int updateId = 2;
        const string username = "unauthorized_user";

        var update = new Update
        {
            Id = updateId,
            Message = new Message
            {
                Text = "Unauthorized message",
                Chat = new Chat
                {
                    Id = chatId,
                    Username = username,
                    FirstName = "Unauthorized User"
                },
                Id = messageId
            }
        };

        _botClientMock
            .SetupSequence(c => c.SendRequest(
                It.IsAny<GetUpdatesRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .Throws(() => new OperationCanceledException());

        // when
        var cts = new CancellationTokenSource();
        var prompts = new List<ChatPrompt>();

        try
        {
            await foreach (var prompt in _chatClient.ReadPrompts(30, cts.Token))
            {
                prompts.Add(prompt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected exception to break out of the infinite loop
        }

        // then
        prompts.Count.ShouldBe(0);

        _botClientMock.Verify(c => c.SendRequest(
            It.Is<GetUpdatesRequest>(x => x.Offset == null && x.Timeout == 30),
            It.IsAny<CancellationToken>()), Times.Once);

        _botClientMock.Verify(c => c.SendRequest(
            It.Is<SendMessageRequest>(x =>
                x.ChatId == chatId &&
                x.Text == "You are not authorized to use this bot." &&
                x.ReplyParameters!.MessageId == messageId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadPrompts_WithMultipleUpdates_IncrementsOffset()
    {
        // given
        var updates = new[]
        {
            new Update
            {
                Id = 1,
                Message = new Message
                {
                    Text = "Message 1",
                    Chat = new Chat
                    {
                        Id = 111,
                        Username = "user1"
                    },
                    Id = 101
                }
            },
            new Update
            {
                Id = 2,
                Message = new Message
                {
                    Text = "Message 2",
                    Chat = new Chat
                    {
                        Id = 111,
                        Username = "user1"
                    },
                    Id = 102
                }
            }
        };

        _botClientMock
            .SetupSequence(c => c.SendRequest(It.IsAny<GetUpdatesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(updates)
            .Throws(() => new OperationCanceledException());

        // when
        var cts = new CancellationTokenSource();
        var prompts = new List<ChatPrompt>();

        try
        {
            await foreach (var prompt in _chatClient.ReadPrompts(30, cts.Token))
            {
                prompts.Add(prompt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected exception to break out of the infinite loop
        }

        // then
        prompts.Count.ShouldBe(2);

        _botClientMock.Verify(c => c.SendRequest(
            It.Is<GetUpdatesRequest>(x => x.Offset == null && x.Timeout == 30),
            It.IsAny<CancellationToken>()), Times.Once);

        _botClientMock.Verify(c => c.SendRequest(
            It.Is<GetUpdatesRequest>(x => x.Offset == 3 && x.Timeout == 30),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReadPrompts_WithReplyToMessage_SetsReplyToMessageId()
    {
        // given
        const long chatId = 123456789;
        const int messageId = 300;
        const int replyToMessageId = 299;
        const int updateId = 3;
        const string messageText = "This is a reply";
        const string username = "user2";

        var update = new Update
        {
            Id = updateId,
            Message = new Message
            {
                Text = messageText,
                Chat = new Chat
                {
                    Id = chatId,
                    Username = username
                },
                Id = messageId,
                ReplyToMessage = new Message
                {
                    Id = replyToMessageId
                }
            }
        };

        _botClientMock
            .SetupSequence(c => c.SendRequest(
                It.IsAny<GetUpdatesRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([update])
            .Throws(() => new OperationCanceledException());

        // when
        var cts = new CancellationTokenSource();
        var prompts = new List<ChatPrompt>();

        try
        {
            await foreach (var prompt in _chatClient.ReadPrompts(30, cts.Token))
            {
                prompts.Add(prompt);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected exception to break out of the infinite loop
        }

        // then
        prompts.Count.ShouldBe(1);
        prompts[0].ReplyToMessageId.ShouldBe(replyToMessageId);
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