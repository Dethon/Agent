using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerLibrary.Services;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.Domain.Downloads.Vfs;
using Xunit;

namespace Tests.Unit.McpServerLibrary;

public class DownloadCompletionWatcherTests
{
    [Fact]
    public async Task Sweep_CompletedDownload_EmitsAndRemovesEntry()
    {
        var (client, routing, emitter) = Build();
        client.Add(DownloadFakes.Item(42, DownloadState.Completed));
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.Count.ShouldBe(1);
        emitter.Emitted[0].ConversationId.ShouldBe("conv-42");
        routing.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_InProgressDownload_DoesNothing()
    {
        var (client, routing, emitter) = Build();
        client.Add(DownloadFakes.Item(42, DownloadState.InProgress));
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.ShouldBeEmpty();
        routing.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_EmitFails_RetainsEntryForRetry()
    {
        var (client, routing, emitter) = Build();
        emitter.EmitResult = false;
        client.Add(DownloadFakes.Item(42, DownloadState.Completed));
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.Count.ShouldBe(1);
        emitter.Emitted[0].ConversationId.ShouldBe("conv-42");
        routing.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_VanishedTorrent_DropsEntrySilently()
    {
        var (client, routing, emitter) = Build();
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.ShouldBeEmpty();
        routing.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_NoActiveSessions_DoesNotTouchAnything()
    {
        var (client, routing, emitter) = Build();
        emitter.HasActiveSessions = false;
        client.Add(DownloadFakes.Item(42, DownloadState.Completed));
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.ShouldBeEmpty();
        routing.Entries.Count.ShouldBe(1);
    }

    private static (DownloadFakes.FakeDownloadClient, DownloadFakes.FakeRoutingStore, FakeEmitter) Build() =>
        (new DownloadFakes.FakeDownloadClient(), new DownloadFakes.FakeRoutingStore(), new FakeEmitter());

    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id,
        Title = $"Title {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "fran", new ReplyTarget("signalr", $"conv-{id}"))
    };

    private static DownloadCompletionWatcher Watcher(
        DownloadFakes.FakeDownloadClient client, DownloadFakes.FakeRoutingStore routing, FakeEmitter emitter) =>
        new(routing, client, emitter, Settings(), NullLogger<DownloadCompletionWatcher>.Instance);

    private static McpSettings Settings() => new()
    {
        Jackett = new JackettConfiguration { ApiKey = "x", ApiUrl = "x" },
        QBittorrent = new QBittorrentConfiguration { ApiUrl = "x", UserName = "x", Password = "x" },
        DownloadLocation = "/downloads",
        BaseLibraryPath = "/media",
        RedisConnectionString = "unused"
    };

    private sealed class FakeEmitter : IDownloadNotificationEmitter
    {
        public bool HasActiveSessions { get; set; } = true;
        public bool EmitResult { get; set; } = true;
        public List<ChannelMessageNotification> Emitted { get; } = new();

        public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
        {
            Emitted.Add(payload);
            return Task.FromResult(EmitResult);
        }
    }
}