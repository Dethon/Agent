using Channels.Hosting;
using Domain.Channels;
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

        var items = await emitter.DrainAsync();
        items.Count.ShouldBe(1);
        items[0].Message!.ConversationId.ShouldBe("conv-42");
        routing.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_InProgressDownload_DoesNothing()
    {
        var (client, routing, emitter) = Build();
        client.Add(DownloadFakes.Item(42, DownloadState.InProgress));
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        (await emitter.DrainAsync()).ShouldBeEmpty();
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

        (await emitter.DrainAsync()).ShouldBeEmpty();
        routing.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sweep_VanishedTorrent_DropsEntrySilently()
    {
        var (client, routing, emitter) = Build();
        routing.Entries.Add(Routing(42));

        await Watcher(client, routing, emitter).SweepAsync(CancellationToken.None);

        (await emitter.DrainAsync()).ShouldBeEmpty();
        routing.Entries.ShouldBeEmpty();
    }

    private static (DownloadFakes.FakeDownloadClient, DownloadFakes.FakeRoutingStore, InboxProbe) Build(
        bool live = true) =>
        (new DownloadFakes.FakeDownloadClient(), new DownloadFakes.FakeRoutingStore(), new InboxProbe(live));

    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id,
        Title = $"Title {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "fran", new ReplyTarget("signalr", $"conv-{id}"))
    };

    private static DownloadCompletionWatcher Watcher(
        DownloadFakes.FakeDownloadClient client, DownloadFakes.FakeRoutingStore routing, InboxProbe emitter) =>
        new(routing, client, emitter.Emitter, Settings(), NullLogger<DownloadCompletionWatcher>.Instance);

    private static McpSettings Settings() => new()
    {
        Jackett = new JackettConfiguration { ApiKey = "x", ApiUrl = "x" },
        QBittorrent = new QBittorrentConfiguration { ApiUrl = "x", UserName = "x", Password = "x" },
        DownloadLocation = "/downloads",
        BaseLibraryPath = "/media",
        RedisConnectionString = "unused"
    };

    // A real inbox behind the real emitter rather than a substitute for it, so what these tests
    // observe is what a subscriber would actually receive. "Live" is expressed the way production
    // expresses it — whether anyone has polled.
    private sealed class InboxProbe
    {
        private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "library";
        private readonly ChannelInbox _inbox = new();

        public InboxProbe(bool live)
        {
            if (live)
            {
                _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None).GetAwaiter().GetResult();
            }

            Emitter = new ChannelNotificationEmitter(_inbox, DeliveryPolicy.GateOnLive);
        }

        public ChannelNotificationEmitter Emitter { get; }

        public Task<IReadOnlyList<ChannelInboxItem>> DrainAsync() =>
            _inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
    }
}