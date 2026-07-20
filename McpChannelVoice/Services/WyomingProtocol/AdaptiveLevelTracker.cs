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
    TimeSpan floorWindow,
    TimeSpan? floorSmoothing = null,
    double? demoteMarginDb = null)
{
    private readonly double _clampDb = ToDb(clampRms);
    private readonly double _floorSmoothingMs = (floorSmoothing ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds;
    private readonly Queue<(double DurationMs, double RmsDb)> _window = new();
    private readonly Queue<(double DurationMs, double Energy)> _smoothing = new();
    private double _windowMs;
    private double _smoothingMs;
    private double _peakDb = double.NegativeInfinity;
    private bool _active;

    public double FloorDb { get; private set; }

    public double FloorRms => Math.Pow(10, FloorDb / 20);

    // Capture-level accept test: did anything speech-classified stand above the CONVERGED
    // floor by the demote margin? A floor seeded during a background lull (TV inter-phrase
    // gap) lets resumed background latch as "speech" until the min-window converges; that
    // pseudo-speech sits AT the converged floor, while real near-field speech sits well
    // above it. The floor (windowed min) is the reference, not the trailing-run mean: the
    // mean sits 2-4 dB above the min, and that gap was measured in the field demoting a
    // real command whose AGC-compressed peak was only 11 dB over a loud-TV floor. The
    // margin defaults to the entry margin — anything that latched under an already-
    // converged floor then passes by construction — but is independently tunable.
    // False until any frame has classified as speech (_peakDb is NegativeInfinity).
    public bool SpeechProminent => _peakDb >= FloorDb + (demoteMarginDb ?? enterMarginDb);

    public bool IsSpeech(double rms, double durationMs)
    {
        var rmsDb = ToDb(rms);
        UpdateFloor(rms, durationMs);

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

    private void UpdateFloor(double rms, double durationMs)
    {
        if (durationMs <= 0)
        {
            // A zero-duration frame (malformed/empty payload) would enqueue without ever
            // advancing _windowMs/_smoothingMs (unbounded queue growth) and its rms of 0
            // would slam the floor to 0 dB for a full window — drop it instead of letting
            // it enter either accumulator.
            return;
        }

        // Feed the min-window with SMOOTHED energy, not the raw chunk level. Real TV dialog
        // is bursty: it drops to near room silence for 100-400 ms between phrases, and a
        // raw-min floor latches onto those lulls (measured in the field: FloorRms 72-97 with
        // the TV on), so TV bursts read as speech and the adaptive gate never engages. A
        // duration-weighted moving average of chunk ENERGY (mean square) over a trailing
        // smoothing window rides at the TV's speaking level through sub-window lulls —
        // energy-domain averaging is dominated by the loud chunks — while a sustained quiet
        // stretch (>= the smoothing window) still pulls it down within about one window.
        var energy = rms * rms;
        _smoothing.Enqueue((durationMs, energy));
        _smoothingMs += durationMs;
        while (_smoothing.Count > 1 && _smoothingMs - _smoothing.Peek().DurationMs >= _floorSmoothingMs)
        {
            _smoothingMs -= _smoothing.Dequeue().DurationMs;
        }
        var smoothedEnergy = _smoothing.Sum(e => e.Energy * e.DurationMs) / _smoothingMs;
        var smoothedDb = 10 * Math.Log10(Math.Max(smoothedEnergy, 1));

        _window.Enqueue((durationMs, smoothedDb));
        _windowMs += durationMs;
        while (_window.Count > 1 && _windowMs - _window.Peek().DurationMs >= floorWindow.TotalMilliseconds)
        {
            _windowMs -= _window.Dequeue().DurationMs;
        }
        FloorDb = _window.Min(e => e.RmsDb);
    }

    private static double ToDb(double rms) => 20 * Math.Log10(Math.Max(rms, 1));
}