using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerLibrary.Services;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.Domain.Downloads.Vfs;
using Tests.Unit.Mcp.Hosting;
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

        var items = emitter.Received();
        items.Count.ShouldBe(1);
        items[0].ConversationId.ShouldBe("conv-42");
        routing.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_InProgressDownload_DoesNothing()
    {
        var (client, routing, emitter) = Build();
        client.Add(DownloadFakes.Item(42, DownloadState.InProgress));
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Received().ShouldBeEmpty();
        routing.Entries.Count.ShouldBe(1);
    }

    // Gate-on-live: with nobody listening the emit buffers nothing and reports false, so the
    // routing entry stays for the retry and no duplicate alert is waiting behind it.
    [Fact]
    public async Task Sweep_NoLiveSubscriber_RetainsEntryAndBuffersNothing()
    {
        var (client, routing, emitter) = Build(live: false);
        client.Add(DownloadFakes.Item(42, DownloadState.Completed));
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Received().ShouldBeEmpty();
        routing.Entries.Count.ShouldBe(1);
    }

    // One warning per outage, not one per pending download per tick: an overnight disconnection
    // with a single completed download used to produce thousands of identical lines. The retry
    // semantics are untouched — the routing entry stays either way.
    [Fact]
    public async Task Sweep_AgentStaysDownAcrossTicks_WarnsOnce()
    {
        var (client, routing, emitter) = Build(live: false);
        client.Add(DownloadFakes.Item(42, DownloadState.Completed));
        routing.Entries.Add(Routing(42));
        using var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var watcher = Watcher(client, routing, emitter, logs);

        await watcher.SweepAsync(CancellationToken.None);
        await watcher.SweepAsync(CancellationToken.None);
        await watcher.SweepAsync(CancellationToken.None);

        logs.Messages.ShouldHaveSingleItem().ShouldContain("until delivery resumes");
    }

    // The other half of the muted warning: exactly one line says the outage ended, and a healthy
    // watcher never mentions it at all.
    [Fact]
    public async Task Sweep_DeliveryResumes_SaysSoOnce()
    {
        var (client, routing, emitter) = Build(live: false);
        client.Add(DownloadFakes.Item(42, DownloadState.Completed));
        client.Add(DownloadFakes.Item(43, DownloadState.Completed));
        routing.Entries.Add(Routing(42));
        routing.Entries.Add(Routing(43));
        using var logs = CapturingLoggerProvider.ForLevel(LogLevel.Information);
        var watcher = Watcher(client, routing, emitter, logs);
        await watcher.SweepAsync(CancellationToken.None);

        emitter.GoLive();
        await watcher.SweepAsync(CancellationToken.None);

        logs.Messages.Count(m => m.Contains("resumed")).ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_VanishedTorrent_DropsEntrySilently()
    {
        var (client, routing, emitter) = Build();
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        emitter.Received().ShouldBeEmpty();
        routing.Entries.ShouldBeEmpty();
    }

    private static (DownloadFakes.FakeDownloadClient, DownloadFakes.FakeRoutingStore, ChannelInboxProbe) Build(
        bool live = true) =>
        (new DownloadFakes.FakeDownloadClient(), new DownloadFakes.FakeRoutingStore(), new ChannelInboxProbe("library", DeliveryPolicy.GateOnLive, live));

    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id,
        Title = $"Title {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "fran", new ReplyTarget("signalr", $"conv-{id}"))
    };

    private static DownloadCompletionWatcher Watcher(
        DownloadFakes.FakeDownloadClient client,
        DownloadFakes.FakeRoutingStore routing,
        ChannelInboxProbe emitter,
        CapturingLoggerProvider? logs = null) =>
        new(
            routing,
            client,
            emitter.Emitter,
            Settings(),
            logs is null
                ? NullLogger<DownloadCompletionWatcher>.Instance
                : new LoggerFactory([logs]).CreateLogger<DownloadCompletionWatcher>());

    private static McpSettings Settings() => new()
    {
        Jackett = new JackettConfiguration { ApiKey = "x", ApiUrl = "x" },
        QBittorrent = new QBittorrentConfiguration { ApiUrl = "x", UserName = "x", Password = "x" },
        DownloadLocation = "/downloads",
        BaseLibraryPath = "/media",
        RedisConnectionString = "unused"
    };
}