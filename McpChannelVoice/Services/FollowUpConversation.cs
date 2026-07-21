using System.Threading.Channels;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Per-connection turn-taking over one held-open Wyoming wake stream. Runs on its own task;
// the read loop calls OnWake/OnAudioStop and routes audio into the session capture this opens.
// I/O is injected as delegates so the loop is unit-testable without TCP, STT, or playback.
public sealed class FollowUpConversation(
    FollowUpSettings followUp,
    TimeProvider time) : IDisposable
{
    private readonly Channel<bool> _wakes = Channel.CreateUnbounded<bool>();
    private readonly CancellationTokenSource _disposed = new();
    private volatile UtteranceCapture? _first;
    private volatile bool _active;

    // Opens a capture on the session (returns it) — isFollowUp selects the no-speech window.
    public required Func<bool, UtteranceCapture> OpenCapture { get; init; }
    public required Action CloseCapture { get; init; }

    // Transcribe the captured audio and dispatch it to the agent. Receives the whole capture so
    // the dispatcher can read gate stats (peak RMS, speech ms) alongside the audio. Returns false
    // when nothing reached the agent (empty/low-confidence transcript, no session) — there will be
    // no reply, so the loop must end the conversation instead of waiting on a handshake that never
    // settles.
    public required Func<UtteranceCapture, bool, CancellationToken, Task<bool>> TranscribeAndDispatch { get; init; }

    // Enqueue the chime and return once it has drained.
    public required Func<CancellationToken, Task> EnqueueChime { get; init; }

    // Write the closing transcript to the satellite (stops streaming, re-arms wake).
    public required Func<CancellationToken, Task> EndConversation { get; init; }

    // Reset / await the per-turn "did the agent speak?" handshake.
    public required Action ResetTurn { get; init; }
    public required Func<Task<bool>> AwaitReply { get; init; }

    // Side effect (metric) just before a follow-up window opens.
    public required Func<CancellationToken, Task> OnFollowUpWindow { get; init; }

    // Side effect (metric) when a capture ends without accepted speech — the window expired
    // or the gate demoted a background-only capture. Receives the rejected capture's gate
    // stats so the host can publish them (rejection tuning needs field data).
    public required Func<CaptureStats, CancellationToken, Task> OnSilenceTimeout { get; init; }

    // Side effect (metric/log) when a dispatched turn's reply never resolves within ReplyTimeoutMs.
    public Func<CancellationToken, Task> OnReplyTimeout { get; init; } = _ => Task.CompletedTask;

    // Early-close speaker check. If > 0, a capture still running at this mark is verified on the
    // audio so far; an unknown voice (EarlyReject returns true) closes it immediately instead of
    // holding the mic open to trailing silence / max-utterance. Enrolled voices (or too little
    // speech / no verifier) keep capturing, so real commands are never truncated. 0 disables.
    public int EarlyVerifyMs { get; init; }
    public Func<UtteranceCapture, CancellationToken, Task<bool>> EarlyReject { get; init; } =
        (_, _) => Task.FromResult(false);

    public void OnWake()
    {
        if (_active || _disposed.IsCancellationRequested)
        {
            return;
        }
        _active = true;
        _first = OpenCapture(false);
        _wakes.Writer.TryWrite(true);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _wakes.Reader.ReadAsync(token);
                await RunConversationAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection tearing down.
        }
    }

    private async Task RunConversationAsync(CancellationToken ct)
    {
        try
        {
            var capture = _first!;
            var turns = 0;

            while (!ct.IsCancellationRequested)
            {
                var outcome = await AwaitCaptureAsync(capture, ct);
                CloseCapture();

                if (outcome is null)
                {
                    // Unknown voice caught mid-capture by the early-close check: end now and
                    // re-arm wake rather than holding the mic open to the natural end.
                    await EndConversation(ct);
                    return;
                }

                if (outcome == CaptureOutcome.NoSpeech)
                {
                    await OnSilenceTimeout(capture.Stats, ct);
                    await EndConversation(ct);
                    return;
                }

                var isFollowUp = turns > 0;
                ResetTurn();
                var dispatched = await TranscribeAndDispatch(capture, isFollowUp, ct);

                // Nothing reached the agent (or follow-up is off): no reply will resolve the turn,
                // so end now rather than blocking the loop on a handshake that never settles.
                if (!dispatched || !followUp.Enabled)
                {
                    await EndConversation(ct);
                    return;
                }

                bool spoke;
                try
                {
                    spoke = await AwaitReply().WaitAsync(TimeSpan.FromMilliseconds(followUp.ReplyTimeoutMs), time, ct);
                }
                catch (TimeoutException)
                {
                    // Reply never came (agent down, no session, playback preempted/failed). Recover
                    // the satellite instead of leaving the mic stream wedged open indefinitely.
                    await OnReplyTimeout(ct);
                    await EndConversation(ct);
                    return;
                }

                if (!spoke || turns >= followUp.MaxTurns)
                {
                    await EndConversation(ct);
                    return;
                }

                if (followUp.Chime)
                {
                    await EnqueueChime(ct);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(followUp.PlaybackTailMs), time, ct);

                turns++;
                await OnFollowUpWindow(ct);
                capture = OpenCapture(true);
            }
        }
        finally
        {
            // Reset before RunAsync re-blocks on ReadAsync, so a wake arriving after the conversation
            // ends is never dropped (it sees _active=false, opens a capture, and enqueues a token the
            // loop will read). _active stays true only DURING a conversation, when no new wake frame
            // can arrive (the satellite streams continuously until it receives the closing transcript).
            _active = false;
        }
    }

    // Awaits the capture end; if EarlyVerifyMs elapses first, runs the speaker check on the audio
    // so far. Returns null when that check rejects an unknown voice (the caller ends the turn),
    // otherwise the natural outcome. Disabled (EarlyVerifyMs <= 0) awaits the end directly.
    private async Task<CaptureOutcome?> AwaitCaptureAsync(UtteranceCapture capture, CancellationToken ct)
    {
        if (EarlyVerifyMs <= 0)
        {
            return await capture.Completed.WaitAsync(ct);
        }

        try
        {
            return await capture.Completed.WaitAsync(TimeSpan.FromMilliseconds(EarlyVerifyMs), time, ct);
        }
        catch (TimeoutException)
        {
            if (await EarlyReject(capture, ct))
            {
                return null;
            }
            return await capture.Completed.WaitAsync(ct);
        }
    }

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }
}