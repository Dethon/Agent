using System.Net;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public async Task TranscribeAsync_ReturnsTextAndLanguage()
    {
        var stub = new StubHandler("""{"text":"hola","language":"es"}""");
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.openai.com") };
        var sut = new OpenAiSpeechToText(http, model: "whisper-1", apiKey: "sk-test",
            NullLogger<OpenAiSpeechToText>.Instance);

        async IAsyncEnumerable<AudioChunk> Audio()
        {
            yield return new AudioChunk { Data = new byte[160], Format = AudioFormat.WyomingStandard };
            await Task.Yield();
        }

        var result = await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);
        result.Text.ShouldBe("hola");
        result.Language.ShouldBe("es");
        stub.LastRequest!.Headers.Authorization!.Parameter.ShouldBe("sk-test");
    }
}