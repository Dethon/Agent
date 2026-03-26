using McpChannelTelegram.McpTools;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;

namespace Tests.Unit.McpChannelTelegram;

public class SendReplyToolTests
{
    private readonly Mock<ITelegramBotClient> _botClient = new();
    private readonly MessageAccumulator _accumulator = new();
    private readonly IServiceProvider _services;

    public SendReplyToolTests()
    {
        var botRegistry = new BotRegistry(new Dictionary<string, ITelegramBotClient>
        {
            ["jack"] = _botClient.Object
        });
        botRegistry.RegisterChatAgent(100, "jack");

        _services = new ServiceCollection()
            .AddSingleton(botRegistry)
            .AddSingleton(_accumulator)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task Run_WithNonTextContentType_ReturnsOkWithoutSending()
    {
        var reasoningResult = await SendReplyTool.McpRun("100:100", "thinking...", "reasoning", false, null, _services);
        var toolCallResult = await SendReplyTool.McpRun("100:100", """{"Name":"mcp:server:search","Arguments":{"query":"test"}}""", "tool_call", false, null, _services);

        reasoningResult.ShouldBe("ok");
        toolCallResult.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Error_SendsErrorAndFlushesAccumulated()
    {
        _accumulator.Append("100:100", "partial text");

        var result = await SendReplyTool.McpRun("100:100", "something broke", "error", false, null, _services);

        result.ShouldBe("ok");
        // Two sends: one for accumulated text, one for error
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task McpRun_StreamComplete_FlushesAccumulatedText()
    {
        _accumulator.Append("100:100", "full response");

        var result = await SendReplyTool.McpRun("100:100", "", "stream_complete", true, null, _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_TextNotComplete_AccumulatesWithoutSending()
    {
        var result = await SendReplyTool.McpRun("100:100", "chunk1", "text", false, "msg-1", _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task McpRun_TextComplete_FlushesAndSends()
    {
        _accumulator.Append("100:100", "chunk1");

        var result = await SendReplyTool.McpRun("100:100", "chunk2", "text", true, "msg-1", _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_ForumThread_PassesThreadId()
    {
        // chatId:threadId where threadId != chatId means forum thread
        var result = await SendReplyTool.McpRun("100:42", "hello", "text", true, null, _services);
        _accumulator.Append("100:42", "hello");

        result.ShouldBe("ok");
    }

    [Fact]
    public async Task McpRun_NonForumChat_PassesNullThreadId()
    {
        // chatId:threadId where threadId == chatId means non-forum
        var result = await SendReplyTool.McpRun("100:100", "hello", "text", true, null, _services);

        result.ShouldBe("ok");
    }

    [Fact]
    public async Task McpRun_UnknownChat_ThrowsInvalidOperation()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => SendReplyTool.McpRun("999:999", "hello", "text", true, null, _services));
    }

    [Fact]
    public async Task McpRun_StreamComplete_NoAccumulated_DoesNotSend()
    {
        var result = await SendReplyTool.McpRun("100:100", "", "stream_complete", true, null, _services);

        result.ShouldBe("ok");
        _botClient.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}