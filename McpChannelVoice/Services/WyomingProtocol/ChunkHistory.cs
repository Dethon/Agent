namespace McpChannelVoice.Services.WyomingProtocol;

public sealed record ChunkSample(long Timestamp, double Rms, bool IsSpeech);

public sealed record CaptureActivity(long OpenedAt, IReadOnlyList<ChunkSample> Samples);

// Rolling per-chunk acoustic memory of one capture, so the wake arbiter can ask retrospectively
// "what did this mic hear during another satellite's wake-word span?". Written on the Wyoming
// read loop (Feed), snapshotted on the arbiter's decision task — hence the lock.
public sealed class ChunkHistory(TimeProvider time, TimeSpan span)
{
    private readonly Queue<ChunkSample> _samples = new();
    private readonly Lock _gate = new();

    public long OpenedAt { get; } = time.GetTimestamp();

    public void Record(double rms, bool isSpeech)
    {
        var now = time.GetTimestamp();
        var horizon = now - (long)(span.TotalSeconds * time.TimestampFrequency);
        lock (_gate)
        {
            _samples.Enqueue(new ChunkSample(now, rms, isSpeech));
            while (_samples.Count > 0 && _samples.Peek().Timestamp < horizon)
            {
                _samples.Dequeue();
            }
        }
    }

    public IReadOnlyList<ChunkSample> Snapshot()
    {
        lock (_gate)
        {
            return _samples.ToArray();
        }
    }
}