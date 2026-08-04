using System.Text.Json.Nodes;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

// One run of the hub's link to a satellite: registering it, launching the playback and conversation
// tasks, routing the frames the satellite sends, and unwinding in order when the link ends. It is
// the thing that runs — nothing outside holds a reference to it, and a dropped link ends it for
// good. The satellite session is the other half of the same lifetime facing the other way: the
// satellite as something the rest of the hub can address by id.
//
// The wire arrives as two halves. The write side is a delegate supplied at construction, because the
// conversation coordinator's end-of-turn write and the arbiter's re-arm handle both close over it
// before the read loop starts. The read side is an async sequence handed to the run: the host keeps
// the Wyoming client and disposes it when the run returns, so dialling and disposal stay together.
public sealed class SatelliteConnection(
    SatelliteSessionRegistry sessions,
    WakeArbiter arbiter,
    ActiveAlertRegistry alerts,
    TimeProvider time,
    ILogger logger)
{
    private Task? _playbackTask;
    private Task? _conversationTask;
    // Per-turn, and only ever touched from the single read loop below: did run-pipeline already
    // announce this turn? Cleared at audio-stop, which is exactly where the satellite ends the mic
    // stream (transcript or pause-satellite both route through its end_capture).
    private bool _wakeAnnounced;

    public required SatelliteSession Session { get; init; }

    // The connection's microphone. Exposed beside the session because a caller watching this link
    // wants to know whether it is listening, which is a fact about the mic rather than the session.
    public Microphone Mic => Session.Mic;

    public required FollowUpConversation Coordinator { get; init; }

    public required Func<WyomingEvent, CancellationToken, Task> Writer { get; init; }

    // A playback job's failure. Bound by the host, which is where the conversation id the metric
    // carries can be resolved.
    public required Func<Exception, Task> OnPlaybackError { get; init; }

    private string Id => Session.SatelliteId;

    // Throws when the link drops, so the host's reconnect loop catches and retries.
    public async Task RunAsync(IAsyncEnumerable<WyomingEvent> events, CancellationToken ct)
    {
        try
        {
            Start(ct);
            await Writer(WyomingEvent.Header("run-satellite", new JsonObject()), ct);

            await foreach (var evt in events.WithCancellation(ct))
            {
                Route(evt);
            }
        }
        finally
        {
            ReleaseArbitration();
            await DrainAsync();
        }
    }

    // Registration and launch together: the arbiter handle and the two background tasks all close
    // over the same writer, and a partial one of these is what the drain's null checks exist for.
    private void Start(CancellationToken ct)
    {
        // The Wyoming client lives only in the host's per-connection scope, so hand the session a
        // writer for control events raised from outside it — the transcript fast-path and the
        // insistent alert hold.
        Session.ControlWriter = Writer;
        sessions.Register(Session);
        // Both re-arm writes share the connection's single writer with the playback loop and the
        // coordinator's end-of-turn write, which already write to it concurrently — the arbiter is a
        // third caller under the same guarantees, not a new sharing model.
        arbiter.Register(Id, new WakeArbiterHandle(
            SatelliteIdentity.Of(Session),
            Session.Config.RmsOffsetDb,
            () => Session.SupportsPause,
            () => Session.Mic.Activity,
            () => Session.Mic.TryAbort(),
            token => Writer(WyomingEvent.Header("pause-satellite", new JsonObject()), token),
            token => Writer(ClosingTranscript(), token)));

        _playbackTask = Task.Run(() => Session.Playback.RunAsync(
            WritePlaybackFrameAsync,
            ct, time, logger,
            onAudioStart: (format, alert, token) => Writer(
                WyomingEvent.Header("audio-start", BuildAudioStart(format, alert)), token),
            onAudioStop: token => Writer(
                WyomingEvent.Header("audio-stop", new JsonObject { ["timestamp"] = 0 }), token),
            onError: (_, ex) => OnPlaybackError(ex)), ct);

        _conversationTask = Task.Run(() => Coordinator.RunAsync(ct), ct);
    }

    private void Route(WyomingEvent evt)
    {
        switch (evt.Type)
        {
            // The only frame that carries wake metadata. nabu-satellite sends exactly this one per
            // turn; other Wyoming satellites may follow it with audio-start.
            case "run-pipeline":
                // Waking the satellite during an active alert dismisses it — no spoken command
                // needed (the satellite mics only on local wake).
                Session.NoteDismissals(alerts.Acknowledge(Id), time.GetUtcNow());
                var wake = WakeAnnouncement.Read(evt.Data);
                if (wake.Rms is not null)
                {
                    Session.MarkSupportsPause();
                }
                _wakeAnnounced = true;
                arbiter.Claim(Id, wake.Rms, wake.Score, wake.Source);
                Coordinator.OnWake(wake);
                break;

            // Legacy/foreign satellites announce the mic stream with audio-start, so it still has to
            // open a turn. It carries no wake metadata, and deliberately does not claim once
            // run-pipeline has announced this turn: a null-rms claim only survives the arbiter's
            // first-wins in-window dedupe if run-pipeline happens to arrive first, so a satellite
            // that reordered the two would silently lose every steal. It announces the wake with no
            // announcement, and the coordinator's early return discards a run-pipeline arriving
            // second rather than letting it report a loudness against a turn it did not open.
            case "audio-start":
                Session.NoteDismissals(alerts.Acknowledge(Id), time.GetUtcNow());
                if (!_wakeAnnounced)
                {
                    arbiter.Claim(Id, null, null, "wake");
                }
                Coordinator.OnWake(null);
                break;

            case "audio-chunk":
                var (rate, width, channels) = FormatOf(evt.Data);
                Session.Mic.Feed(ToChunk(evt.Payload, rate, width, channels));
                break;

            case "audio-stop":
                _wakeAnnounced = false;
                Session.Mic.ForceEnd();
                break;

            case "error":
                logger.LogWarning("Satellite {Id} reported error: {Message}",
                    Id, evt.Data["text"]?.GetValue<string>());
                break;
        }
    }

    // Synchronous, and that is the whole point: a dropped connection must stop being an arbitration
    // candidate before anything unbounded runs, and a method that cannot await cannot be reordered
    // behind one. Everything the drain does is unbounded — the playback loop can be parked writing
    // to the very socket that just died — and until this has run the dying session is still a Rule B
    // holder candidate whose capture history is still populated (on the cancellation path the
    // coordinator never reaches its capture close). A live satellite waking in that window would be
    // suppressed as a leak in favour of a satellite that is already gone.
    private void ReleaseArbitration() => arbiter.Unregister(Id);

    private async Task DrainAsync()
    {
        Coordinator.Dispose();
        Session.Playback.Complete();
        await AwaitSwallowingAsync(_playbackTask);
        // After the loop has stopped, never before: a job whose audio finished as the link died has
        // already earned its outcome, and this is only for what the loop never got to.
        Session.Playback.DiscardUnplayed();
        await AwaitSwallowingAsync(_conversationTask);
        Session.ControlWriter = null;
        sessions.Unregister(Id);
    }

    // Null-guarded because setup can genuinely throw partway: a registration that failed before
    // reaching a Task.Run never produced a task to await. Draining only what was started is what
    // stops a half-built connection leaving a registered session holding a writer over a client the
    // host has already disposed.
    private static async Task AwaitSwallowingAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        { await task; }
        catch { /* unwinds on cancellation / disconnect */ }
    }

    private static WyomingEvent ClosingTranscript() =>
        WyomingEvent.Header("transcript", new JsonObject { ["text"] = string.Empty });

    private Task WritePlaybackFrameAsync(AudioChunk chunk, CancellationToken ct)
    {
        var data = new JsonObject
        {
            ["rate"] = chunk.Format.SampleRateHz,
            ["width"] = chunk.Format.SampleWidthBytes,
            ["channels"] = chunk.Format.Channels
        };
        return Writer(WyomingEvent.WithPayload("audio-chunk", data, chunk.Data), ct);
    }

    private static AudioChunk ToChunk(ReadOnlyMemory<byte> payload, int rate, int width, int channels) => new()
    {
        Data = payload,
        Format = new AudioFormat { SampleRateHz = rate, SampleWidthBytes = width, Channels = channels },
        Timestamp = TimeSpan.Zero
    };

    // `alert` tells the satellite to play this stream on its non-attenuated alert route, bypassing
    // the per-satellite voice level. Emitted on every stream, not only alerts, so a wire trace shows
    // the routing explicitly; a pre-1.5 satellite ignores the unknown field.
    internal static JsonObject BuildAudioStart(AudioFormat format, bool alert) => new()
    {
        ["rate"] = format.SampleRateHz,
        ["width"] = format.SampleWidthBytes,
        ["channels"] = format.Channels,
        ["timestamp"] = 0,
        ["alert"] = alert
    };

    private static (int Rate, int Width, int Channels) FormatOf(JsonObject data) =>
    (
        JsonNumber.ReadInt(data, "rate", AudioFormat.WyomingStandard.SampleRateHz),
        JsonNumber.ReadInt(data, "width", AudioFormat.WyomingStandard.SampleWidthBytes),
        JsonNumber.ReadInt(data, "channels", AudioFormat.WyomingStandard.Channels)
    );
}