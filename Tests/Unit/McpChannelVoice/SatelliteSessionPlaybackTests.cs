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

    [Fact]
    public async Task RunPlaybackLoop_JobAudioThrows_SurvivesAndReportsThenPlaysNext()
    {
        var session = MakeSession();
        var played = new List<string>();
        var errors = new List<string>();

        var failing = new PlaybackJob(
            Label: "failing",
            Priority: AnnouncePriority.Normal,
            Audio: ThrowingAudio(),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        var next = failing with { Label = "next", Audio = GenerateAudio("next", count: 1) };

        var pumpTask = session.RunPlaybackLoopAsync(
            async (chunk, ct) =>
            {
                played.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None,
            onError: (job, ex) =>
            {
                errors.Add(job.Label);
                return Task.CompletedTask;
            });

        await session.EnqueuePlaybackAsync(failing, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(next, queueMaxDepth: 4);
        session.CompletePlayback();

        await pumpTask;

        errors.ShouldBe(["failing"]);
        played.ShouldBe(["next"]);
    }

    [Fact]
    public async Task RunPlaybackLoop_OnStartedThrows_SwallowsAndKeepsLoopAlive()
    {
        var session = MakeSession();
        var played = new List<string>();

        var bad = new PlaybackJob(
            Label: "bad-onstarted",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("bad", count: 1),
            OnStarted: _ => throw new InvalidOperationException("metrics down"),
            OnPreempted: _ => Task.CompletedTask);
        var next = bad with
        {
            Label = "next",
            Audio = GenerateAudio("next", count: 1),
            OnStarted = _ => Task.CompletedTask
        };

        var pumpTask = session.RunPlaybackLoopAsync(
            async (chunk, ct) =>
            {
                played.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None);

        await session.EnqueuePlaybackAsync(bad, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(next, queueMaxDepth: 4);
        session.CompletePlayback();

        await pumpTask;

        // A failing OnStarted (e.g. metrics publish down) is swallowed: the job's audio still plays
        // and the loop continues to the next job rather than tearing down.
        played.ShouldBe(["bad", "next"]);
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

    private static async IAsyncEnumerable<AudioChunk> ThrowingAudio()
    {
        await Task.Yield();
        throw new InvalidOperationException("synthesis failed");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}