using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tts;

// Streams Kokoro synthesis from Lemonade's OpenAI-compatible /audio/speech endpoint.
// stream_format=audio + response_format=pcm returns raw 24 kHz mono s16le incrementally
// (the Kokoros backend synthesizes ~10-word chunks and Lemonade forwards them unbuffered),
// so audio starts playing before the whole utterance is synthesized. Each block is resampled
// to the satellites' fixed 22 050 Hz sink. Raw reads are not 2-byte aligned: a 0/1-byte
// remainder is carried between reads so a partial int16 never reaches the resampler.
public sealed class OpenAiTextToSpeech(
    IHttpClientFactory httpFactory,
    OpenAiTtsConfig config,
    ILogger<OpenAiTextToSpeech> logger) : ITextToSpeech
{
    private const int SourceRateHz = 24000;
    private static readonly AudioFormat _outputFormat = new()
    {
        SampleRateHz = 22050,
        SampleWidthBytes = 2,
        Channels = 1
    };

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = config.Model,
            ["input"] = text,
            ["voice"] = options.Voice ?? config.Voice,
            ["speed"] = config.Speed,
            ["response_format"] = "pcm",
            ["stream_format"] = "audio"
        };

        // Per-call client keeps factory handler rotation working; it lives until the iterator
        // completes, so it spans the whole streamed read.
        using var http = httpFactory.CreateClient(LemonadeHttp.ClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{config.BaseUrl.TrimEnd('/')}/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        // ResponseHeadersRead + incremental reads keep the response streaming end to end; a non-2xx
        // throws before any audio is yielded so the playback loop's onError/OnFailed path fires.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var resampler = new PcmStreamResampler(SourceRateHz, _outputFormat.SampleRateHz);
        var buffer = new byte[8192];
        var carried = 0;
        var yielded = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carried, buffer.Length - carried), ct);
            if (read == 0)
            {
                break;
            }

            var available = carried + read;
            var whole = available & ~1;
            var resampled = resampler.Process(buffer.AsSpan(0, whole));

            carried = available - whole;
            if (carried > 0)
            {
                buffer[0] = buffer[whole];
            }

            if (resampled.Length > 0)
            {
                yielded = true;
                yield return new AudioChunk { Data = resampled, Format = _outputFormat };
            }
        }

        if (carried > 0)
        {
            logger.LogWarning("Kokoro PCM stream ended mid-sample; dropped trailing byte");
        }

        // A Kokoro failure can close the stream cleanly with zero PCM; treating that as success
        // would play nothing and end the turn as an invisible "assistant didn't answer". Throw so
        // the playback loop's OnFailed path fires instead.
        if (!yielded)
        {
            throw new InvalidOperationException("Kokoro synthesis returned no audio");
        }
        logger.LogDebug("Kokoro synthesis complete");
    }
}