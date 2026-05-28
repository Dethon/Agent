using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class OpenRouterSpeechToTextTests
{
    private sealed class StubHandler(string body, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
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
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://openrouter.ai") };
        var sut = new OpenRouterSpeechToText(http, model: "openai/whisper-1", apiKey: "or-test",
            Mock.Of<IMetricsPublisher>(), NullLogger<OpenRouterSpeechToText>.Instance);

        var result = await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("hola");
        result.Language.ShouldBe("es");
        stub.LastRequest!.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        stub.LastRequest!.Headers.Authorization!.Parameter.ShouldBe("or-test");
    }

    [Fact]
    public async Task TranscribeAsync_BodyIsBase64JsonNotMultipart()
    {
        var stub = new StubHandler("""{"text":"hola","language":"es"}""");
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://openrouter.ai") };
        var sut = new OpenRouterSpeechToText(http, model: "openai/whisper-1", apiKey: "or-test",
            Mock.Of<IMetricsPublisher>(), NullLogger<OpenRouterSpeechToText>.Instance);

        await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);

        stub.LastContentType.ShouldNotBeNull();
        stub.LastContentType.ShouldStartWith("application/json");
        stub.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/api/v1/audio/transcriptions");

        using var doc = JsonDocument.Parse(stub.LastRequestBody!);
        doc.RootElement.GetProperty("model").GetString().ShouldBe("openai/whisper-1");
        var inputAudio = doc.RootElement.GetProperty("input_audio");
        inputAudio.GetProperty("format").GetString().ShouldBe("wav");
        var data = inputAudio.GetProperty("data").GetString();
        data.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TranscribeAsync_PublishesTokenUsageEventWithOriginVoice()
    {
        var stub = new StubHandler("""{"text":"hola","language":"es"}""");
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://openrouter.ai") };
        var publisher = new Mock<IMetricsPublisher>();
        TokenUsageEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as TokenUsageEvent)
            .Returns(Task.CompletedTask);

        var sut = new OpenRouterSpeechToText(http, model: "openai/whisper-1", apiKey: "or-test",
            publisher.Object, NullLogger<OpenRouterSpeechToText>.Instance);

        await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        captured.ShouldNotBeNull();
        captured.Origin.ShouldBe("voice");
        captured.Model.ShouldBe("openai/whisper-1");
    }
}