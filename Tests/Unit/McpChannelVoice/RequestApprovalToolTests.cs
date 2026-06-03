using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class RequestApprovalToolTests
{
    private readonly SatelliteSession _session;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ApprovalCaptureBroker _broker = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly VoiceConversationManager _manager;
    private readonly string _conversationId;
    private readonly IServiceProvider _services;

    public RequestApprovalToolTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-kitchen");
                var topic = new TopicMetadata("topic-kitchen", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        _manager = new VoiceConversationManager(
            factory.Object, _accumulator, new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        _conversationId = _manager.GetOrCreateAsync(_session, "agent-1", "hi", default).GetAwaiter().GetResult();

        _tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_broker)
            .AddSingleton(_accumulator)
            .AddSingleton(_manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(new VoiceSettings())
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton<ILogger<RequestApprovalTool>>(NullLogger<RequestApprovalTool>.Instance)
            .BuildServiceProvider();
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    private static ToolApprovalRequest MakeRequest(string toolName = "mcp__lib__download") =>
        new(null, toolName, new Dictionary<string, object?>());

    [Fact]
    public async Task NotifyMode_DoesNotSpeakOrWaitForResponse()
    {
        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify,
            [MakeRequest()],
            _services);

        result.ShouldBe("notified");
        // Auto-approved tool calls must not be narrated over voice — with no pending
        // reply text there is nothing to speak (the tool name itself is never read out).
        _tts.Verify(
            t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyMode_WithPendingReplyText_SpeaksAcknowledgement()
    {
        // The agent wrote an acknowledgement before calling the (auto-approved) tool.
        _accumulator.Append(_conversationId, "Dame un momento");

        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify,
            [MakeRequest()],
            _services);

        result.ShouldBe("notified");
        // The pending acknowledgement is spoken now so the user hears it while the tool runs.
        _tts.Verify(
            t => t.SynthesizeAsync("Dame un momento", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestMode_PositiveAnswer_ReturnsApproved()
    {
        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request,
            [MakeRequest()],
            _services);

        await Task.Delay(50);
        _broker.SubmitUtterance("kitchen-01", "sí, claro");

        var result = await run;
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task RequestMode_AmbiguousThenNegative_ReturnsDeclined()
    {
        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request,
            [MakeRequest()],
            _services);

        await Task.Delay(50);
        _broker.SubmitUtterance("kitchen-01", "maybe");
        await Task.Delay(50);
        _broker.SubmitUtterance("kitchen-01", "no thanks");

        var result = await run;
        result.ShouldBe("declined");
    }

    [Fact]
    public async Task RequestMode_TwoAmbiguous_DeclinesByDefault()
    {
        var run = RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Request,
            [MakeRequest()],
            _services);

        await Task.Delay(50);
        _broker.SubmitUtterance("kitchen-01", "maybe");
        await Task.Delay(50);
        _broker.SubmitUtterance("kitchen-01", "hmm");

        var result = await run;
        result.ShouldBe("declined");
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsDeclined()
    {
        var result = await RequestApprovalTool.McpRun(
            "ghost-01:999", ApprovalMode.Request, [MakeRequest()], _services);

        result.ShouldBe("declined");
    }

    [Fact]
    public async Task McpRun_Notify_ResolvesSatelliteFromCompositeConversationId()
    {
        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify, new List<ToolApprovalRequest>(), _services);

        result.ShouldBe("notified");
    }
}