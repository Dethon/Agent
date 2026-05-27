using System.Net;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class OpenAiTextToSpeechTests
{
    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wave");
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task SynthesizeAsync_StreamsBackChunkedPcm()
    {
        var pcm = Enumerable.Range(0, 4800).Select(i => (byte)(i & 0xff)).ToArray();
        var http = new HttpClient(new StubHandler(pcm)) { BaseAddress = new Uri("https://api.openai.com") };
        var sut = new OpenAiTextToSpeech(http, model: "tts-1", voice: "alloy", apiKey: "sk-test",
            Mock.Of<IMetricsPublisher>(), NullLogger<OpenAiTextToSpeech>.Instance);

        var collected = new List<byte>();
        await foreach (var chunk in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
            collected.AddRange(chunk.Data.ToArray());
        }

        collected.Count.ShouldBe(pcm.Length);
    }

    [Fact]
    public async Task SynthesizeAsync_PublishesTokenUsageEventWithOriginVoice()
    {
        var pcm = new byte[1024];
        var http = new HttpClient(new StubHandler(pcm)) { BaseAddress = new Uri("https://api.openai.com") };
        var publisher = new Mock<IMetricsPublisher>();
        TokenUsageEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as TokenUsageEvent)
            .Returns(Task.CompletedTask);

        var sut = new OpenAiTextToSpeech(http, model: "tts-1", voice: "alloy", apiKey: "sk-test",
            publisher.Object, NullLogger<OpenAiTextToSpeech>.Instance);

        await foreach (var _ in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
        }

        captured.ShouldNotBeNull();
        captured.Origin.ShouldBe("voice");
        captured.Model.ShouldBe("tts-1");
        captured.InputTokens.ShouldBe("hola".Length);
    }
}