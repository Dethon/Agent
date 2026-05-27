using System.Net;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class OpenAiSpeechToTextTests
{
    private sealed class StubHandler(string body, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                await request.Content.ReadAsStringAsync(ct);
            }
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    private static async IAsyncEnumerable<AudioChunk> Audio(int bytes = 160)
    {
        yield return new AudioChunk { Data = new byte[bytes], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsTextAndLanguage()
    {
        var stub = new StubHandler("""{"text":"hola","language":"es"}""");
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.openai.com") };
        var sut = new OpenAiSpeechToText(http, model: "whisper-1", apiKey: "sk-test",
            Mock.Of<IMetricsPublisher>(), NullLogger<OpenAiSpeechToText>.Instance);

        var result = await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);
        result.Text.ShouldBe("hola");
        result.Language.ShouldBe("es");
        stub.LastRequest!.Headers.Authorization!.Parameter.ShouldBe("sk-test");
    }

    [Fact]
    public async Task TranscribeAsync_PublishesTokenUsageEventWithOriginVoice()
    {
        var stub = new StubHandler("""{"text":"hola","language":"es","duration":1.5}""");
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.openai.com") };
        var publisher = new Mock<IMetricsPublisher>();
        TokenUsageEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as TokenUsageEvent)
            .Returns(Task.CompletedTask);

        var sut = new OpenAiSpeechToText(http, model: "whisper-1", apiKey: "sk-test",
            publisher.Object, NullLogger<OpenAiSpeechToText>.Instance);

        await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Origin.ShouldBe("voice");
        captured.Model.ShouldBe("whisper-1");
    }
}