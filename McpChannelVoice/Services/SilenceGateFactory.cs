using System.Collections.Concurrent;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// How a satellite's endpointing gate is put together, in one place. Every live capture on a
// satellite — the wake turn, a follow-up, an approval answer — asks for its gate here, so none of
// them can resolve it differently. There is deliberately no gate-purpose parameter: the call sites
// want the same gate, and a parameter that only ever takes one value is somewhere for them to
// diverge again. A real difference arrives with a test that names it.
public sealed class SilenceGateFactory(
    VoiceSettings voice, WyomingClientSettings wyoming, TimeProvider time)
{
    // Keyed by satellite rather than held per connection: a room does not change because the TCP
    // link blipped, and a reconnect is exactly when a satellite is least able to measure itself.
    private readonly ConcurrentDictionary<string, RoomNoiseMemory> _roomNoise = new();

    public SilenceGate Create(string satelliteId, SatelliteConfig config) => new(
        new AdaptiveLevelTracker(
            config.ResolveRmsThreshold(wyoming),
            config.ResolveEnterMarginDb(wyoming),
            config.ResolveExitMarginDb(wyoming),
            config.ResolvePeakDropDb(wyoming),
            TimeSpan.FromMilliseconds(config.ResolveFloorWindowMs(wyoming)),
            demoteMarginDb: config.ResolveDemoteMarginDb(wyoming),
            // The capture cannot measure the background it opens on top of, so the quietest room
            // reading this satellite has produced recently caps it.
            roomRms: MemoryFor(satelliteId).Rms),
        TimeSpan.FromMilliseconds(wyoming.TrailingSilenceMs),
        TimeSpan.FromMilliseconds(wyoming.MaxUtteranceMs),
        TimeSpan.FromMilliseconds(config.ResolveMinSpeechMs(wyoming)),
        // Same no-speech window on the wake turn as on follow-ups and approvals: a capture with no
        // speech (false trigger, user changes their mind) must re-arm after WindowMs instead of
        // holding the mic open until the far-larger max-utterance cap.
        noSpeechTimeout: TimeSpan.FromMilliseconds(voice.FollowUp.WindowMs));

    // The satellite's own idle reading, reported on run-pipeline. It describes the room the user is
    // about to speak into better than anything the hub has.
    public void RecordRoomLevel(string satelliteId, double rms) => MemoryFor(satelliteId).Record(rms);

    public void RecordCaptureClose(string satelliteId, CaptureStats stats) =>
        RecordRoomLevel(satelliteId, RoomSampleOf(stats));

    private RoomNoiseMemory MemoryFor(string satelliteId) => _roomNoise.GetOrAdd(
        satelliteId,
        _ => new RoomNoiseMemory(
            time, wyoming.RoomLevelSamples, TimeSpan.FromSeconds(wyoming.RoomLevelRetentionSeconds)));

    // What a finished capture learned about the room. A capture that heard no speech spent its
    // whole window measuring the background, so its own reading is the sample; one that ended on
    // trailing silence measured it over the run that ended it. Anything else (abandoned to
    // arbitration, forced, capped at max-utterance) never established what silence sounded like,
    // and returns 0 — RoomNoiseMemory drops it, exactly as it drops an absent room_rms.
    private static double RoomSampleOf(CaptureStats stats) => stats.EndReason switch
    {
        "no_speech" => stats.MeasuredFloorRms,
        "trailing_silence" => stats.TrailingSilenceMs > 0 ? stats.TrailingRms : 0,
        _ => 0
    };
}