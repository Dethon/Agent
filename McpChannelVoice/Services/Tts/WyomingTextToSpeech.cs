using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tts;

public sealed class WyomingTextToSpeech(
    WyomingTtsConfig config,
    ILogger<WyomingTextToSpeech> logger) : ITextToSpeech
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var client = new WyomingClient();
        await client.ConnectAsync(config.Host, config.Port, ct);

        var voice = options.Voice ?? config.Voice;
        var data = new JsonObject { ["text"] = text };
        if (voice is not null)
        {
            data["voice"] = new JsonObject { ["name"] = voice };
        }
        await client.WriteAsync(WyomingEvent.Header("synthesize", data), ct);

        AudioFormat? format = null;

        await foreach (var evt in client.ReadAllAsync(ct))
        {
            if (evt.Type == "audio-start")
            {
                format = new AudioFormat
                {
                    SampleRateHz = WyomingNumber.ReadInt(evt.Data, "rate", 22050),
                    SampleWidthBytes = WyomingNumber.ReadInt(evt.Data, "width", 2),
                    Channels = WyomingNumber.ReadInt(evt.Data, "channels", 1)
                };
                continue;
            }
            if (evt.Type == "audio-chunk" && evt.Payload.Length > 0)
            {
                yield return new AudioChunk
                {
                    Data = evt.Payload,
                    Format = format ?? AudioFormat.WyomingStandard
                };
                continue;
            }
            if (evt.Type == "audio-stop")
            {
                logger.LogDebug("Piper synthesis complete");
                yield break;
            }
            if (evt.Type == "error")
            {
                // A Wyoming 'error' event (e.g. Piper failed) otherwise falls through and the stream
                // ends with no audio-stop, yielding a silent successful empty synthesis. Throw so the
                // playback loop's onError/OnFailed path fires (TtsError metric) instead of masking it.
                var message = evt.Data["text"]?.GetValue<string>() ?? "unknown error";
                logger.LogWarning("Wyoming TTS reported error: {Message}", message);
                throw new InvalidOperationException($"Wyoming TTS error: {message}");
            }
        }
    }
}