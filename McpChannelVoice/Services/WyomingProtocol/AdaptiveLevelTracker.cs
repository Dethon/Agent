namespace McpChannelVoice.Services.WyomingProtocol;

// Speech/silence classification with measured references instead of a fixed bar.
// A fixed absolute RMS threshold encodes "the room noise floor is below X"; a TV in
// the room violates that permanently, so trailing silence never accumulates and the
// capture only ends at the max-utterance cap. A single far-field mic offers two
// measurable references:
//  - the noise floor: a windowed minimum of chunk levels, seeded by the first real
//    audio. It falls instantly when the room gets quieter (music duck engaging) and
//    rises only as quiet frames age out of the window (duck restore, TV scene
//    change), so the user's own speech cannot drag it up — word gaps and breaths
//    keep re-seeding the true background. Seeding from real audio (no grace period)
//    is deliberate: background above the clamp must read as silence from chunk one,
//    or it would latch minSpeech and end the turn before the user speaks.
//  - the utterance peak: near-field speech sits 15-25 dB above a far TV, so frames
//    far enough below the loudest speech of the turn are background regardless of
//    what the floor estimate believes. Armed only in the adaptive regime so it can
//    never clip loud-then-soft speech in a quiet room.
// The absolute threshold survives as a lower clamp: in a quiet room both hysteresis
// thresholds collapse to it, reproducing the legacy single-threshold gate exactly.
// dB ratios survive AGC gain shifts that absolute values don't.
public sealed class AdaptiveLevelTracker(
    double clampRms,
    double enterMarginDb,
    double exitMarginDb,
    double peakDropDb,
    TimeSpan floorWindow)
{
    private readonly double _clampDb = ToDb(clampRms);
    private readonly Queue<(double DurationMs, double RmsDb)> _window = new();
    private double _windowMs;
    private double _peakDb = double.NegativeInfinity;
    private bool _active;

    public double FloorDb { get; private set; }

    public double FloorRms => Math.Pow(10, FloorDb / 20);

    public bool IsSpeech(double rms, double durationMs)
    {
        var rmsDb = ToDb(rms);
        UpdateFloor(rmsDb, durationMs);

        // Two-threshold hysteresis: enter high, exit low. In a quiet room both
        // collapse to the clamp, reproducing the legacy single-threshold gate.
        var threshold = Math.Max(_clampDb, FloorDb + (_active ? exitMarginDb : enterMarginDb));
        var adaptiveRegime = FloorDb + enterMarginDb > _clampDb;
        // Backstop compares against the peak BEFORE this frame: on the first-ever speech
        // frame _peakDb is still NegativeInfinity, so the backstop can't self-trigger.
        _active = rmsDb >= threshold && !(adaptiveRegime && _peakDb - rmsDb > peakDropDb);
        // Only a speech-classified frame may raise the "utterance peak" — a loud non-speech
        // transient (e.g. capture opening on a click) must not poison the backstop and
        // suppress genuine speech that follows once the transient ages out of the floor.
        if (_active)
        {
            _peakDb = Math.Max(_peakDb, rmsDb);
        }
        return _active;
    }

    private void UpdateFloor(double rmsDb, double durationMs)
    {
        if (durationMs <= 0)
        {
            // A zero-duration frame (malformed/empty payload) would enqueue without ever
            // advancing _windowMs (unbounded queue growth) and its rms of 0 would slam the
            // floor to 0 dB for a full window — drop it instead of letting it enter.
            return;
        }
        _window.Enqueue((durationMs, rmsDb));
        _windowMs += durationMs;
        while (_window.Count > 1 && _windowMs - _window.Peek().DurationMs >= floorWindow.TotalMilliseconds)
        {
            _windowMs -= _window.Dequeue().DurationMs;
        }
        FloorDb = _window.Min(e => e.RmsDb);
    }

    private static double ToDb(double rms) => 20 * Math.Log10(Math.Max(rms, 1));
}