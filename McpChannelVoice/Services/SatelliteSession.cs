using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class SatelliteSession
{
    private static readonly TimeSpan _snoozeWindow = TimeSpan.FromSeconds(60);
    private readonly Lock _dismissGate = new();
    private string? _dismissedAlert;
    private DateTimeOffset _dismissedAt;

    // The queue and the microphone are built by the connection, which is where the queue's depth
    // limits are configured and where the one gate factory lives. A session built without them — a
    // test, or a caller that never listens or plays audio — gets both on the settings' own defaults
    // rather than null properties.
    public SatelliteSession(
        string satelliteId,
        SatelliteConfig config,
        PlaybackQueue? playback = null,
        Microphone? mic = null)
    {
        SatelliteId = satelliteId;
        Config = config;
        Playback = playback ?? new PlaybackQueue();
        Mic = mic ?? new Microphone(satelliteId, new SilenceGateFactory(
            new VoiceSettings(), new WyomingClientSettings(), TimeProvider.System));
    }

    public string SatelliteId { get; }
    public SatelliteConfig Config { get; }

    // The current user turn's reply state. Exposed as a property rather than forwarded through this
    // class: forwarding methods would be a pass-through layer with the same surface the module was
    // extracted to remove.
    public VoiceTurn Turn { get; } = new();

    // Everything queued to be heard on this satellite. Exposed the same way as the turn above, for
    // the same reason.
    public PlaybackQueue Playback { get; }

    // This satellite's microphone, owned the same way. The session has no capture surface of its
    // own: a caller that listens holds the microphone, or CaptureSession if what it is opening is
    // a turn.
    public Microphone Mic { get; }

    // Writes a control event on this satellite's live Wyoming connection. Set by
    // SatelliteConnection when the connection is established and cleared on teardown, because the
    // WyomingClient itself lives only inside that per-connection scope. Null means not connected.
    public Func<WyomingEvent, CancellationToken, Task>? ControlWriter { get; set; }

    // This satellite's own text-to-speech voice, or the channel's. Written once here because the
    // rule was spelled out at four call sites — the reply, both confirmation-prompt paths and the
    // announcement service — so a satellite with its own voice could be honoured in three of them
    // and missed in the fourth. The alarm controller deliberately does not use it: it synthesises
    // each alert once and replays it to every target, so there is no per-satellite voice to resolve.
    public string? ResolveVoice(VoiceSettings settings) =>
        Config.Tts?.OpenAi?.Voice ?? settings.Tts.OpenAi.Voice;

    public async Task<bool> TrySendControlAsync(WyomingEvent evt, CancellationToken ct)
    {
        var writer = ControlWriter;
        if (writer is null)
        {
            return false;
        }

        try
        {
            await writer(evt, ct);
            return true;
        }
        catch (Exception)
        {
            // A control event is best-effort: the connection may be tearing down underneath us, and
            // a failed volume step must not take out the caller's path (a transcript dispatch or an
            // alarm loop). Callers log the false.
            return false;
        }
    }

    // A connection that has ever reported wake_rms runs post-arbitration firmware and understands
    // pause-satellite; anything else gets the legacy transcript abort (audible done cue).
    public bool SupportsPause { get; private set; }
    public void MarkSupportsPause() => SupportsPause = true;

    // Composes what was dismissed into the description the next transcript carries, and stashes it.
    // Lives here, next to the stash it feeds, because the connection reports dismissals from the
    // wake frame while the transcript path reports them after a dispatch — two callers that would
    // otherwise each own a copy of this formatting.
    public void NoteDismissals(IReadOnlyList<DismissedAlert> dismissed, DateTimeOffset now)
    {
        if (dismissed.Count == 0)
        {
            return;
        }
        NoteDismissedAlert(
            string.Join(" and ", dismissed.Select(d => $"{d.Kind.ToString().ToLowerInvariant()} \"{d.Text}\"")),
            now);
    }

    // Wake-word dismissal context for LLM-mediated snooze: the host stashes what was dismissed; the
    // next dispatched transcript within the window consumes it (single-use).
    public void NoteDismissedAlert(string description, DateTimeOffset now)
    {
        lock (_dismissGate)
        {
            _dismissedAlert = description;
            _dismissedAt = now;
        }
    }

    public string? TryConsumeDismissedAlert(DateTimeOffset now)
    {
        lock (_dismissGate)
        {
            var value = _dismissedAlert is not null && now - _dismissedAt <= _snoozeWindow
                ? _dismissedAlert
                : null;
            _dismissedAlert = null;
            return value;
        }
    }
}