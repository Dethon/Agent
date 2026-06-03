using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Voice;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class RequestApprovalToolTests
{
    private static IServiceProvider BuildServices(out SatelliteSession session, out ApprovalCaptureBroker broker, out ReplyTextAccumulator accumulator, ITextToSpeech tts)
    {
        session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        var sessions = new SatelliteSessionRegistry();
        sessions.Register(session);

        broker = new ApprovalCaptureBroker();
        accumulator = new ReplyTextAccumulator();

        return new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton(broker)
            .AddSingleton(accumulator)
            .AddSingleton(tts)
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
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out _, out _, out _, tts.Object);

        var result = await RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Notify,
            [MakeRequest()],
            services);

        result.ShouldBe("notified");
        // Auto-approved tool calls must not be narrated over voice — with no pending
        // reply text there is nothing to speak (the tool name itself is never read out).
        tts.Verify(
            t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyMode_WithPendingReplyText_SpeaksAcknowledgement()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out _, out _, out var accumulator, tts.Object);
        // The agent wrote an acknowledgement before calling the (auto-approved) tool.
        accumulator.Append("kitchen-01", "Dame un momento");

        var result = await RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Notify,
            [MakeRequest()],
            services);

        result.ShouldBe("notified");
        // The pending acknowledgement is spoken now so the user hears it while the tool runs.
        tts.Verify(
            t => t.SynthesizeAsync("Dame un momento", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestMode_PositiveAnswer_ReturnsApproved()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out var session, out var broker, out _, tts.Object);

        var run = RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Request,
            [MakeRequest()],
            services);

        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "sí, claro");

        var result = await run;
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task RequestMode_AmbiguousThenNegative_ReturnsDeclined()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out _, out var broker, out _, tts.Object);

        var run = RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Request,
            [MakeRequest()],
            services);

        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "maybe");
        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "no thanks");

        var result = await run;
        result.ShouldBe("declined");
    }

    [Fact]
    public async Task RequestMode_TwoAmbiguous_DeclinesByDefault()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out _, out var broker, out _, tts.Object);

        var run = RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Request,
            [MakeRequest()],
            services);

        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "maybe");
        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "hmm");

        var result = await run;
        result.ShouldBe("declined");
    }
}