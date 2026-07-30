using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

public enum CaptureOutcome
{
    Ended,
    NoSpeech,
    Abandoned
}

// Audio-level facts about one capture, published on UtteranceTranscribed metrics so the
// RMS/min-speech entry bar and the adaptive-floor margins can be tuned from real data
// instead of guesswork.
// MeasuredFloorRms is the gate's own reading of the background before the remembered room level
// caps it (FloorRms is what the gate actually used). It is what RoomNoiseMemory learns from, so a
// remembered level is never re-derived from itself.
public readonly record struct CaptureStats(
    double PeakRms, double FloorRms, long SpeechMs, string? EndReason, double TrailingRms = 0,
    long TrailingSilenceMs = 0, double MeasuredFloorRms = 0);

// One bounded mic capture over the held-open Wyoming stream. The read loop pushes audio
// via Feed (single-threaded); the gate decides when speech ends (Ended) or the no-speech
// window expires (NoSpeech). Completed settles exactly once; Audio replays the buffered chunks.
public sealed class UtteranceCapture(SilenceGate gate, ChunkHistory? history = null)
{
    private readonly Channel<AudioChunk> _chunks = Channel.CreateUnbounded<AudioChunk>();
    private readonly TaskCompletionSource<CaptureOutcome> _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _forced;
    private readonly List<AudioChunk> _audio = [];

    public Task<CaptureOutcome> Completed => _done.Task;

    public IAsyncEnumerable<AudioChunk> Audio => _chunks.Reader.ReadAllAsync();

    public ChunkHistory? History => history;

    // The full continuous capture — every fed chunk, buffered so the speaker verifier embeds
    // enrollment-matching continuous audio (silence-cut speech-only fragments collapse CAM++
    // similarity). Returned as a snapshot under lock: the early-close check reads it mid-capture
    // on the conversation task while Feed appends on the Wyoming read loop.
    public IReadOnlyList<AudioChunk> BufferedAudio
    {
        get { lock (_audio) { return _audio.ToArray(); } }
    }

    public CaptureStats Stats => new(
        gate.PeakRms,
        gate.FloorRms,
        (long)gate.SpeechElapsed.TotalMilliseconds,
        _forced ? "forced" : gate.EndReason,
        gate.TrailingRms,
        (long)gate.TrailingSilence.TotalMilliseconds,
        gate.MeasuredFloorRms);

    public void Feed(AudioChunk chunk)
    {
        var decision = gate.Process(
            chunk.Data.Span, chunk.Format.SampleRateHz, chunk.Format.SampleWidthBytes, chunk.Format.Channels);
        history?.Record(gate.LastChunkRms, gate.LastChunkWasSpeech);
        lock (_audio)
        {
            _audio.Add(chunk);
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

    // Arbitration loss/steal: settle as Abandoned so the conversation loop exits without
    // dispatching and without its own wire write (the arbiter owns the pause). Returns false
    // when the capture already ended naturally — the caller must then leave the turn alone.
    public bool Abort()
    {
        if (!_done.TrySetResult(CaptureOutcome.Abandoned))
        {
            return false;
        }
        _chunks.Writer.TryComplete();
        return true;
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