using System.Threading.Channels;
using Domain.DTOs.Voice;
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

    // Transcribe the captured audio and dispatch it to the agent.
    public required Func<IAsyncEnumerable<AudioChunk>, bool, CancellationToken, Task> TranscribeAndDispatch { get; init; }

    // Enqueue the chime and return once it has drained.
    public required Func<CancellationToken, Task> EnqueueChime { get; init; }

    // Write the closing transcript to the satellite (stops streaming, re-arms wake).
    public required Func<CancellationToken, Task> EndConversation { get; init; }

    // Reset / await the per-turn "did the agent speak?" handshake.
    public required Action ResetTurn { get; init; }
    public required Func<Task<bool>> AwaitReply { get; init; }

    // Side effect (metric) just before a follow-up window opens.
    public required Func<CancellationToken, Task> OnFollowUpWindow { get; init; }

    // Side effect (metric) when a follow-up window expires with no speech.
    public required Func<CancellationToken, Task> OnSilenceTimeout { get; init; }

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
                var outcome = await capture.Completed.WaitAsync(ct);
                CloseCapture();

                if (outcome == CaptureOutcome.NoSpeech)
                {
                    await OnSilenceTimeout(ct);
                    await EndConversation(ct);
                    return;
                }

                var isFollowUp = turns > 0;
                ResetTurn();
                await TranscribeAndDispatch(capture.Audio, isFollowUp, ct);

                if (!followUp.Enabled)
                {
                    await EndConversation(ct);
                    return;
                }

                var spoke = await AwaitReply().WaitAsync(ct);
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

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }
}