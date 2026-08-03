using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

// The microphone for one connection's turn-taking: opening it, closing it and reading its
// statistics at exactly that moment, and the two events that tell the satellite what its indicator
// should show.
//
// Closing a capture and recording what it taught us about the room are one operation here, so a new
// turn-taking path cannot do the first and forget the second. The approval mic is a separate,
// one-shot capture that does not come through here; it takes its gate from the same factory.
public sealed class CaptureSession(
    SatelliteSession session,
    SilenceGateFactory gates,
    TimeProvider time,
    TimeSpan historySpan,
    // Called with isFollowUp as each capture opens. The wake turn is the one the connection host
    // acts on: it is where the satellite's stashed wake metadata is consumed, and a follow-up has no
    // wake of its own.
    Action<bool> onOpened)
{
    public UtteranceCapture Open(bool isFollowUp)
    {
        // The turn opens here, so the playback loop can report turn -> first-audio latency.
        session.MarkTurnStart(time.GetTimestamp());
        onOpened(isFollowUp);
        return session.OpenCapture(
            gates.Create(session.SatelliteId, session.Config),
            // Rule B asks an already-open capture, retrospectively, what it heard during another
            // satellite's wake-word span — so every capture has to remember.
            new ChunkHistory(time, historySpan));
    }

    // Returns the gate statistics frozen at this instant. The endpointing tail is what anchors
    // speech end and it must not be re-read later: Feed keeps accepting frames until the satellite
    // gets its closing transcript, so a later read would report the tail plus an arbitrary delay.
    public CaptureStats Close(UtteranceCapture capture)
    {
        session.CloseCapture();
        var stats = capture.Stats;
        gates.RecordCaptureClose(session.SatelliteId, stats);
        // Stamped with the host's TimeProvider — the same instance handed to RunPlaybackLoopAsync,
        // which reads it back. The frozen endpointing tail rewinds the close to the instant the user
        // actually stopped talking; read here, at the close, because it is the last point where the
        // tail is known to be the one the gate ended on.
        session.MarkSpeechEnd(time.GetTimestamp(), stats.TrailingSilenceMs, time);
        return stats;
    }

    // Tells the satellite the user has stopped speaking and processing has begun — this drives its
    // Thinking indicator.
    public Task SpeechStoppedAsync(CancellationToken ct) =>
        SendIndicatorAsync("voice-stopped", ct);

    // Tells the satellite the mic is live again for a wake-free follow-up turn — this returns its
    // indicator from Thinking to Listening. It cannot infer the moment: its own capture never
    // closed, so from its side a reply draining looks the same whether the agent is mid-answer or
    // done.
    public Task ListeningStartedAsync(CancellationToken ct) =>
        SendIndicatorAsync("listening-started", ct);

    // Both events are indicator-only by contract: a failed write must never cost the user the
    // utterance or the window it was announcing, so they go through the session's best-effort
    // writer, which reports failure as false rather than throwing. An already-cancelled connection
    // still stops the loop here; a cancellation raised mid-write is swallowed with everything else,
    // and the loop's next await on the same token throws instead.
    private Task SendIndicatorAsync(string type, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return session.TrySendControlAsync(WyomingEvent.Header(type, new JsonObject()), ct);
    }
}