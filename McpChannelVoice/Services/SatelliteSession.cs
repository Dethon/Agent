using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed record PlaybackJob(
    string Label,
    AnnouncePriority Priority,
    IAsyncEnumerable<AudioChunk> Audio,
    Func<string, Task> OnStarted,
    Func<string, Task> OnPreempted);

public sealed class SatelliteSession
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly Channel<PlaybackJob> _playback = Channel.CreateUnbounded<PlaybackJob>();
    private CancellationTokenSource? _currentPlaybackCts;
    private readonly Lock _gate = new();

    public SatelliteSession(string satelliteId, SatelliteConfig config)
    {
        SatelliteId = satelliteId;
        Config = config;
    }

    public string SatelliteId { get; }
    public string ConversationId => SatelliteId;
    public SatelliteConfig Config { get; }

    public ValueTask PublishAudioAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        _inbound.Writer.WriteAsync(bytes, ct);

    public void CompleteInboundAudio() => _inbound.Writer.TryComplete();

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadInboundAudioAsync(CancellationToken ct) =>
        _inbound.Reader.ReadAllAsync(ct);

    public async ValueTask<bool> EnqueuePlaybackAsync(PlaybackJob job, int queueMaxDepth)
    {
        if (job.Priority == AnnouncePriority.High)
        {
            PreemptCurrent();
            await _playback.Writer.WriteAsync(job);
            return true;
        }

        if (job.Priority == AnnouncePriority.Low && _playback.Reader.Count > 0)
        {
            return false;
        }

        if (_playback.Reader.Count >= queueMaxDepth)
        {
            return false;
        }
        await _playback.Writer.WriteAsync(job);
        return true;
    }

    public void CompletePlayback() => _playback.Writer.TryComplete();

    public void PreemptCurrent()
    {
        lock (_gate)
        {
            _currentPlaybackCts?.Cancel();
        }
    }

    public async Task RunPlaybackLoopAsync(
        Func<AudioChunk, CancellationToken, Task> writer,
        CancellationToken ct)
    {
        await foreach (var job in _playback.Reader.ReadAllAsync(ct))
        {
            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate)
            { _currentPlaybackCts = jobCts; }

            await job.OnStarted(job.Label);

            try
            {
                await foreach (var chunk in job.Audio.WithCancellation(jobCts.Token))
                {
                    await writer(chunk, jobCts.Token);
                }
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await job.OnPreempted(job.Label);
            }
            finally
            {
                lock (_gate)
                { _currentPlaybackCts = null; }
                jobCts.Dispose();
            }
        }
    }
}