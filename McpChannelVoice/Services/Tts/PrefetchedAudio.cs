using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services.Tts;

// Makes a lazy synthesis stream hot: the TTS request goes out as soon as the segment is queued,
// rather than when the playback loop first pulls it.
//
// Why this exists: the playback loop is a single sequential enumeration, and a job's audio is not
// touched until the previous job's body has completed — including its real-time drain wait. With the
// reply split into sentence segments that put a full TTS round trip (~0.5-0.9 s measured) into every
// seam. Starting the pump at enqueue moves that round trip under the previous segment's playback,
// where it costs nothing.
//
// It deliberately does NOT change when the loop dequeues or how preemption works — those stay exactly
// as they were, which is what keeps a High-priority alarm still able to cut in.
public sealed class PrefetchedAudio : IAsyncDisposable
{
    private readonly Channel<AudioChunk> _buffer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public PrefetchedAudio(IAsyncEnumerable<AudioChunk> source, int capacity)
    {
        // Bounded so a long utterance parks the producer instead of buffering its whole synthesis
        // into memory; the loop draining chunks is what lets it resume.
        _buffer = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _pump = PumpAsync(source);
    }

    public IAsyncEnumerable<AudioChunk> Chunks => Read();

    private async Task PumpAsync(IAsyncEnumerable<AudioChunk> source)
    {
        try
        {
            await foreach (var chunk in source.WithCancellation(_cts.Token))
            {
                await _buffer.Writer.WriteAsync(chunk, _cts.Token);
            }
            _buffer.Writer.TryComplete();
        }
        // Only OUR cancellation is a clean stop. Any other OperationCanceledException falls through
        // to the catch-all below and surfaces, because completing the stream cleanly here would hand
        // the loop a zero-chunk segment: drained, no error, no metric — a silently dropped sentence
        // reported as a success.
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _buffer.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // Surfaced to the consumer on its next read, so a synthesis error still reaches the
            // playback loop, which settles the job as failed instead of hanging whoever awaits it.
            _buffer.Writer.TryComplete(ex);
        }
    }

    private async IAsyncEnumerable<AudioChunk> Read([EnumeratorCancellation] CancellationToken ct = default)
    {
        try
        {
            await foreach (var chunk in _buffer.Reader.ReadAllAsync(ct))
            {
                yield return chunk;
            }
        }
        finally
        {
            // The consumer stopped early (preempted, or the loop tore down), so release the
            // in-flight synthesis rather than leaving the producer parked on a full buffer.
            await _cts.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _pump;
        }
        catch
        {
            // The pump's failure is the consumer's to observe, and a disposal path has no consumer.
        }
        _cts.Dispose();
    }
}