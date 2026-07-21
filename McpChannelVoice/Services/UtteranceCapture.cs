using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

public enum CaptureOutcome
{
    Ended,
    NoSpeech
}

// Audio-level facts about one capture, published on UtteranceTranscribed metrics so the
// RMS/min-speech entry bar and the adaptive-floor margins can be tuned from real data
// instead of guesswork.
public readonly record struct CaptureStats(
    double PeakRms, double FloorRms, long SpeechMs, string? EndReason, double TrailingRms = 0);

// One bounded mic capture over the held-open Wyoming stream. The read loop pushes audio
// via Feed (single-threaded); the gate decides when speech ends (Ended) or the no-speech
// window expires (NoSpeech). Completed settles exactly once; Audio replays the buffered chunks.
public sealed class UtteranceCapture(SilenceGate gate)
{
    private readonly Channel<AudioChunk> _chunks = Channel.CreateUnbounded<AudioChunk>();
    private readonly TaskCompletionSource<CaptureOutcome> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _forced;
    private readonly List<AudioChunk> _speechAudio = [];

    public Task<CaptureOutcome> Completed => _done.Task;

    public IAsyncEnumerable<AudioChunk> Audio => _chunks.Reader.ReadAllAsync();

    // Speech-classified chunks only (per the gate). Feed is single-threaded on the
    // Wyoming read loop; read this after Completed settles.
    public IReadOnlyList<AudioChunk> SpeechAudio => _speechAudio;

    public CaptureStats Stats => new(
        gate.PeakRms,
        gate.FloorRms,
        (long)gate.SpeechElapsed.TotalMilliseconds,
        _forced ? "forced" : gate.EndReason,
        gate.TrailingRms);

    public void Feed(AudioChunk chunk)
    {
        var decision = gate.Process(
            chunk.Data.Span, chunk.Format.SampleRateHz, chunk.Format.SampleWidthBytes, chunk.Format.Channels);
        if (gate.LastFrameWasSpeech)
        {
            _speechAudio.Add(chunk);
        }
        _chunks.Writer.TryWrite(chunk);

        switch (decision)
        {
            case SilenceGate.Decision.EndUtterance:
                _chunks.Writer.TryComplete();
                _done.TrySetResult(CaptureOutcome.Ended);
                break;
            case SilenceGate.Decision.NoSpeech:
                _chunks.Writer.TryComplete();
                _done.TrySetResult(CaptureOutcome.NoSpeech);
                break;
        }
    }

    public void ForceEnd()
    {
        // Feed/ForceEnd are serialized on the same Wyoming read loop: a plain completed-check
        // is enough to stop a late audio-stop from overwriting a natural end's EndReason.
        if (!_done.Task.IsCompleted)
        {
            _forced = true;
        }
        _chunks.Writer.TryComplete();
        _done.TrySetResult(CaptureOutcome.Ended);
    }
}