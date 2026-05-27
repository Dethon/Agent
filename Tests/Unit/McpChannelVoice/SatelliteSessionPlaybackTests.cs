using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionPlaybackTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public async Task EnqueuePlayback_Normal_RunsAfterCurrent()
    {
        var session = MakeSession();
        var played = new List<string>();

        var first = new PlaybackJob(
            Label: "first",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("first", count: 2),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        var second = first with { Label = "second", Audio = GenerateAudio("second", count: 1) };

        var pumpTask = session.RunPlaybackLoopAsync(
            async (chunk, ct) =>
            {
                played.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None);

        await session.EnqueuePlaybackAsync(first, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(second, queueMaxDepth: 4);
        session.CompletePlayback();

        await pumpTask;

        played.ShouldBe(["first", "first", "second"]);
    }

    private static async IAsyncEnumerable<AudioChunk> GenerateAudio(string label, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new AudioChunk
            {
                Data = System.Text.Encoding.UTF8.GetBytes(label),
                Format = AudioFormat.WyomingStandard
            };
            await Task.Yield();
        }
    }
}