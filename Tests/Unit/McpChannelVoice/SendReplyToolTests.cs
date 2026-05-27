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

public class SendReplyToolTests
{
    private readonly SatelliteSession _session;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly IServiceProvider _services;

    public SendReplyToolTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => EmptyAudio(text));

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_tts.Object)
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton(new VoiceSettings())
            .AddSingleton<ILogger<SendReplyTool>>(NullLogger<SendReplyTool>.Instance)
            .BuildServiceProvider();
    }

    private static async IAsyncEnumerable<AudioChunk> EmptyAudio(string label)
    {
        yield return new AudioChunk
        {
            Data = System.Text.Encoding.UTF8.GetBytes(label),
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    [Fact]
    public async Task McpRun_Text_NotComplete_AccumulatesNoSynthesis()
    {
        var result = await SendReplyTool.McpRun("kitchen-01", "hola ", ReplyContentType.Text, false, "m-1", _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_Text_Complete_SynthesisesAccumulatedText()
    {
        await SendReplyTool.McpRun("kitchen-01", "hola ", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun("kitchen-01", "mundo", ReplyContentType.Text, true, "m-1", _services);

        _tts.Verify(t => t.SynthesizeAsync("hola mundo", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Error_SpeaksErrorPrefix()
    {
        await SendReplyTool.McpRun("kitchen-01", "boom", ReplyContentType.Error, true, "m-1", _services);
        _tts.Verify(t => t.SynthesizeAsync(
            It.Is<string>(s => s.Contains("boom")), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Reasoning_DoesNothing()
    {
        var result = await SendReplyTool.McpRun("kitchen-01", "thinking", ReplyContentType.Reasoning, false, null, _services);
        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsOk()
    {
        var result = await SendReplyTool.McpRun("ghost-01", "hi", ReplyContentType.Text, true, "m-1", _services);
        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }
}