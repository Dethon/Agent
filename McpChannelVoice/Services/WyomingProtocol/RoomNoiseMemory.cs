namespace McpChannelVoice.Services.WyomingProtocol;

// What the room sounds like when nobody is talking, per satellite, learned from the captures the
// hub has already run.
//
// AdaptiveLevelTracker can only measure the background from audio inside its own capture, and a
// capture that opens on sound has none: someone running straight from the wake word into their
// command leaves the floor estimating their own voice, 6x above the room in the field. The hub
// does hear the real room, just not at that moment — a capture that heard no speech at all
// measured it for its whole window, and every capture that ends measured it over the trailing run
// that ended it. Remembering the quietest of those recent samples gives the next capture a ceiling
// its own contaminated measurement cannot exceed.
//
// The quietest sample wins rather than the newest: a sample can be inflated by speech the gate
// misread as background, but nothing makes a room read quieter than it actually was. Samples
// expire so a reading stops capping a room it no longer describes (music starts, a fan comes on),
// and only the most recent few are kept so one quiet moment cannot speak for a whole window.
public sealed class RoomNoiseMemory(TimeProvider time, int maxSamples, TimeSpan retention)
{
    private readonly Queue<(DateTimeOffset At, double Rms)> _samples = new();
    private readonly Lock _gate = new();

    // Recorded on the conversation loop as captures close, read on the Wyoming read loop when a
    // wake opens the next one.
    public void Record(double rms)
    {
        // Captures that never accumulated a trailing run report 0 — an absent measurement, not a
        // silent room. Recording it would pin the ceiling at silence for the whole retention.
        if (rms <= 0 || !double.IsFinite(rms))
        {
            return;
        }

        lock (_gate)
        {
            _samples.Enqueue((time.GetUtcNow(), rms));
            while (_samples.Count > maxSamples)
            {
                _samples.Dequeue();
            }
        }
    }

    public double? Rms
    {
        get
        {
            lock (_gate)
            {
                var cutoff = time.GetUtcNow() - retention;
                while (_samples.Count > 0 && _samples.Peek().At < cutoff)
                {
                    _samples.Dequeue();
                }
                return _samples.Count == 0 ? null : _samples.Min(s => s.Rms);
            }
        }
    }
}