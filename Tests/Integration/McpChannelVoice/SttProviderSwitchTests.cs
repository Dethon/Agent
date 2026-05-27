using System.Net;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class SttProviderSwitchTests
{
    private sealed class FixedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"hi","language":"en"}""")
            });
    }

    [Fact]
    public async Task ProviderOpenAi_AdapterTranscribesViaHttp()
    {
        var http = new HttpClient(new FixedHandler()) { BaseAddress = new Uri("https://api.openai.com") };
        ISpeechToText sut = new OpenAiSpeechToText(http, "whisper-1", "sk",
            Mock.Of<IMetricsPublisher>(), NullLogger<OpenAiSpeechToText>.Instance);

        async IAsyncEnumerable<AudioChunk> Audio()
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            await Task.Yield();
        }

        var result = await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);
        result.Text.ShouldBe("hi");
    }
}