using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Voice;

public sealed class OpenAiTextToSpeech(
    HttpClient http,
    string model,
    string voice,
    string apiKey,
    IMetricsPublisher metrics,
    ILogger<OpenAiTextToSpeech> logger) : ITextToSpeech
{
    private const decimal CostPerCharacter = 0.000015m;

    private static readonly AudioFormat _format = new()
    {
        SampleRateHz = 24_000,
        SampleWidthBytes = 2,
        Channels = 1
    };

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech")
        {
            Content = JsonContent.Create(new
            {
                model,
                input = text,
                voice = options.Voice ?? voice,
                response_format = "pcm"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            var slice = new byte[read];
            Array.Copy(buffer, slice, read);
            yield return new AudioChunk { Data = slice, Format = _format };
        }
        logger.LogDebug("OpenAI TTS stream complete");

        await metrics.PublishAsync(new TokenUsageEvent
        {
            Sender = "voice-sat",
            Model = model,
            InputTokens = text.Length,
            OutputTokens = 0,
            Cost = text.Length * CostPerCharacter,
            Origin = "voice"
        }, ct);
    }
}