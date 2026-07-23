namespace McpChannelVoice.Services.WyomingProtocol;

// Speech/silence classification with measured references instead of a fixed bar.
// A fixed absolute RMS threshold encodes "the room noise floor is below X"; a TV in
// the room violates that permanently, so trailing silence never accumulates and the
// capture only ends at the max-utterance cap. A single far-field mic offers two
// measurable references:
//  - the noise floor: a windowed minimum of chunk levels, seeded by the first real
//    audio and then FROZEN once the capture accepts speech (see IsSpeech). It falls
//    instantly when the room gets quieter (music duck engaging) and rises only as
//    quiet frames age out of the window (duck restore, TV scene change). Seeding from
//    real audio (no grace period) is deliberate: background above the clamp must read
//    as silence from chunk one, or it would latch minSpeech and end the turn before
//    the user speaks. The freeze was NOT belt-and-braces: this design originally
//    claimed word gaps and breaths would keep re-seeding the true background, and
//    field measurement in July 2026 proved otherwise once the floor was fed 500 ms
//    smoothed energy — gaps must exceed the smoothing window to register, so anyone
//    talking for longer than floorWindow became their own noise floor.
//  - the utterance peak: near-field speech sits 15-25 dB above a far TV, so frames
//    far enough below the loudest speech of the turn are background regardless of
//    what the floor estimate believes. Armed only in the adaptive regime so it can
//    never clip loud-then-soft speech in a quiet room — a guarantee that holds only
//    because the floor is frozen: an unfrozen floor climbs into the adaptive regime
//    under sustained speech, which armed this backstop against the speaker.
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
    private bool _speechSeen;

    public double FloorDb { get; private set; }

    public double FloorRms => Math.Pow(10, FloorDb / 20);

    // Capture-level accept test: did anything speech-classified stand above the floor by
    // the demote margin? The floor (windowed min) is the reference, not the trailing-run
    // mean: the mean sits 2-4 dB above the min, and that gap was measured in the field
    // demoting a real command whose AGC-compressed peak was only 11 dB over a loud-TV
    // floor. False until any frame has classified as speech (_peakDb is NegativeInfinity).
    //
    // Scope note (2026-07-21): this reads the frozen floor, so it can no longer catch the
    // lull-seeded-TV capture it was written for — that relied on the floor converging up
    // to meet the pseudo-speech, which is the same convergence that truncated real long
    // messages. With demoteMarginDb <= enterMarginDb it now passes by construction for
    // any capture containing accepted speech, since latching required peak >= floor +
    // enterMarginDb against that same frozen floor. It still bites when the demote margin
    // is tuned ABOVE the entry margin. TV rejection lives in speaker verification now.
    public bool SpeechProminent => _peakDb >= FloorDb + (demoteMarginDb ?? enterMarginDb);

    public bool IsSpeech(double rms, double durationMs)
    {
        var rmsDb = ToDb(rms);
        // Freeze the floor once this capture has accepted speech. The floor estimates the
        // BACKGROUND, and after FloorWindowMs of someone talking there is nothing else left
        // in the window to estimate it from: it climbs to their own speaking level, the
        // entry bar (floor + enterMarginDb) rises above their loudest syllable, and live
        // speech reads as silence until the trailing timer ends the turn mid-sentence.
        // Frozen, the reference stays the room as it was before they started — which is
        // what a background estimate means.
        if (!_speechSeen)
        {
            UpdateFloor(rms, durationMs);
        }

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
            _speechSeen = true;
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