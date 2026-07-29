using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class McpAgentConversationContextTests
{
    private static readonly ConversationContext _context = new(
        "jack", "conv-42", "fran", new ReplyTarget("signalr", "conv-42"));

    private static (McpAgent Agent, List<ChatOptions?> Captured, List<string> Logs) CreateAgent()
    {
        var captured = new List<ChatOptions?>();
        var logProvider = new CapturingLoggerProvider();
        var chatClient = new Mock<IChatClient>();
        chatClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (_, options, _) => captured.Add(options))
            .Returns(new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] }
            }.ToAsyncEnumerable());

        var agent = new McpAgent(
            [],
            chatClient.Object,
            "test-agent",
            "",
            new Mock<IThreadStateStore>().Object,
            "fran",
            loggerFactory: LoggerFactory.Create(b => b.AddProvider(logProvider)),
            conversationId: "conv-42");

        return (agent, captured, logProvider.Messages);
    }

    [Fact]
    public async Task RunStreaming_WithStampedMessage_CarriesContextInChatOptions()
    {
        var (agent, captured, logs) = CreateAgent();
        await using var _ = agent;

        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConversationContext(_context);

        await agent.RunStreamingAsync([message]).ToListAsync();

        var options = captured.ShouldHaveSingleItem().ShouldNotBeNull();
        options.AdditionalProperties.ShouldNotBeNull();
        options.AdditionalProperties[ConversationContextMeta.OptionsKey].ShouldBe(_context);
        logs.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunStreaming_WithoutStampedMessage_SetsAdditionalPropertiesAndLogsError()
    {
        // The key must never be dropped quietly: every tools/call of the 2026-07-28 protocol
        // carries its own conversation context, so an unstamped run is a defect that has to be
        // visible in the logs rather than degrade downstream scoping in silence.
        var (agent, captured, logs) = CreateAgent();
        await using var _ = agent;

        await agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "hi")]).ToListAsync();

        var options = captured.ShouldHaveSingleItem().ShouldNotBeNull();
        options.AdditionalProperties.ShouldNotBeNull();
        logs.ShouldContain(m => m.Contains(ChannelProtocol.ConversationContextMetaKey));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}