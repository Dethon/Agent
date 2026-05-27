using System.Net;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
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
            NullLogger<OpenAiTextToSpeech>.Instance);

        var collected = new List<byte>();
        await foreach (var chunk in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
            collected.AddRange(chunk.Data.ToArray());
        }

        collected.Count.ShouldBe(pcm.Length);
    }
}