using System.Diagnostics;
using System.Net;
using Infrastructure.Clients.Torrent;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class JackettSearchClientTests
{
    private const string IndexersXml =
        """
        <indexers>
          <indexer id="fast" configured="true"></indexer>
          <indexer id="slow" configured="true"></indexer>
        </indexers>
        """;

    private static string TorznabResponse(string title) =>
        $"""
         <rss xmlns:torznab="http://torznab.com/schemas/2015/feed">
           <channel>
             <item>
               <title>{title}</title>
               <link>magnet:?xt=urn:btih:{title}</link>
               <size>1000</size>
               <torznab:attr name="seeders" value="5" />
               <torznab:attr name="peers" value="10" />
             </item>
           </channel>
         </rss>
         """;

    private sealed class RoutingHandler(TimeSpan slowIndexerDelay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("t=indexers"))
            {
                return Ok(IndexersXml);
            }

            if (uri.Contains("/indexers/slow/"))
            {
                await Task.Delay(slowIndexerDelay, cancellationToken);
                return Ok(TorznabResponse("slow-result"));
            }

            return Ok(TorznabResponse("fast-result"));
        }

        private static HttpResponseMessage Ok(string content) =>
            new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }

    private static JackettSearchClient CreateClient(RoutingHandler handler, TimeSpan searchDeadline)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        return new JackettSearchClient(httpClient, "apikey", searchDeadline);
    }

    [Fact]
    public async Task Search_IndexerExceedsDeadline_ReturnsAccumulatedResultsWithoutWaiting()
    {
        var handler = new RoutingHandler(slowIndexerDelay: TimeSpan.FromSeconds(8));
        var client = CreateClient(handler, searchDeadline: TimeSpan.FromMilliseconds(500));

        var stopwatch = Stopwatch.StartNew();
        var results = await client.Search("ubuntu", CancellationToken.None);
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
        results.ShouldHaveSingleItem().Title.ShouldBe("fast-result");
    }

    [Fact]
    public async Task Search_AllIndexersWithinDeadline_ReturnsResultsFromAllIndexers()
    {
        var handler = new RoutingHandler(slowIndexerDelay: TimeSpan.Zero);
        var client = CreateClient(handler, searchDeadline: TimeSpan.FromSeconds(10));

        var results = await client.Search("ubuntu", CancellationToken.None);

        results.Length.ShouldBe(2);
        results.Select(x => x.Title).ShouldBe(["fast-result", "slow-result"], ignoreOrder: true);
    }
}