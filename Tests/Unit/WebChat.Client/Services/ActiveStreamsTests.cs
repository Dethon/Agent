using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Services.Streaming;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class ActiveStreamsTests
{
    [Fact]
    public void IsActive_ATrackedStreamStillRunning_IsTrue()
    {
        var streams = new ActiveStreams();
        var running = new TaskCompletionSource();

        streams.Track("topic-1", running.Task);

        streams.IsActive("topic-1").ShouldBeTrue();
        running.SetResult();
    }

    [Fact]
    public async Task Track_TheStreamEnds_TheTopicStopsBeingActive()
    {
        var streams = new ActiveStreams();
        var running = new TaskCompletionSource();
        streams.Track("topic-1", running.Task);

        running.SetResult();

        await TestChat.Eventually(() => !streams.IsActive("topic-1"));
    }

    // The end of a stream is reported after the fact, so it can land once the user has already
    // sent again and a newer stream holds the topic. Forgetting the newer one there would let
    // the next send open a second stream over a live one, duplicating the reply on screen.
    [Fact]
    public void Forget_ANewerStreamHoldsTheTopic_LeavesItActive()
    {
        var streams = new ActiveStreams();
        var older = new TaskCompletionSource();
        var newer = new TaskCompletionSource();
        streams.Track("topic-1", older.Task);
        streams.Track("topic-1", newer.Task);

        streams.Forget("topic-1", older.Task);

        streams.IsActive("topic-1").ShouldBeTrue();
        older.SetResult();
        newer.SetResult();
    }

    [Fact]
    public void Forget_TheStreamStillHoldingTheTopic_StopsItBeingActive()
    {
        var streams = new ActiveStreams();
        var running = new TaskCompletionSource();
        streams.Track("topic-1", running.Task);

        streams.Forget("topic-1", running.Task);

        streams.IsActive("topic-1").ShouldBeFalse();
        running.SetResult();
    }
}