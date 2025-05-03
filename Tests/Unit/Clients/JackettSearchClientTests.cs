using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Infrastructure.Clients;
using Moq;
using Moq.Protected;
using Shouldly;

namespace Tests.Unit.Clients;

public class JackettSearchClientTests
{
    private const string ApiKey = "test-api-key";
    private const string BaseUrl = "http://jackett.test/api/v2.0/";
    private const string DefaultQuery = "test";
    private const string IndexersPath = $"indexers/all/results/torznab/api?apikey={ApiKey}&t=indexers&configured=true";
    private const string LinkBaseUri = "https://example.com";

    private readonly Mock<HttpMessageHandler> _handlerMock;
    private readonly HttpClient _httpClient;
    private readonly JackettSearchClient _client;

    public JackettSearchClientTests()
    {
        _handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _httpClient = new HttpClient(_handlerMock.Object)
        {
            BaseAddress = new Uri(BaseUrl)
        };
        _client = new JackettSearchClient(_httpClient, ApiKey, new Dictionary<string, string>
        {
            { "test_mapping_key", "test_mapping_value" }
        });
    }

    [Fact]
    public async Task Search_WithValidIndexers_ReturnsResults()
    {
        // given
        SetupIndexersResponse(["indexer1", "indexer2"]);

        var indexer1Result = CreateTestResult("Title 1", "movie", $"{LinkBaseUri}/1", 1000, 10, 5);
        var indexer2Result = CreateTestResult("Title 2", "tv", $"{LinkBaseUri}/2", 2000, 20, 10);

        SetupIndexerSearchResponse("indexer1", [indexer1Result]);
        SetupIndexerSearchResponse("indexer2", [indexer2Result]);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        results.Length.ShouldBe(2);
        results.ShouldContain(r => r.Title == "Title 1");
        results.ShouldContain(r => r.Title == "Title 2");
    }

    [Fact]
    public async Task Search_WhenGetIndexersFails_UsesAllIndexer()
    {
        // given
        SetupMockResponse(IndexersPath, HttpStatusCode.InternalServerError, "Error");

        var allIndexerResult = CreateTestResult("Title All", "movie", $"{LinkBaseUri}/all", 3000, 30, 15);
        SetupIndexerSearchResponse("all", [allIndexerResult]);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        var singleResult = results.ShouldHaveSingleItem();
        singleResult.Title.ShouldBe("Title All");
    }

    [Fact]
    public async Task Search_WhenOneIndexerFails_ReturnsResultsFromOtherIndexers()
    {
        // given
        var indexerIds = new[] { "indexer1", "indexer2" };
        SetupIndexersResponse(indexerIds);

        var successResult = CreateTestResult("Title 1", "movie", $"{LinkBaseUri}/1", 1000, 10, 5);
        SetupIndexerSearchResponse("indexer1", [successResult]);

        SetupMockResponse($"indexers/indexer2/results/?apikey={ApiKey}&Query={DefaultQuery}",
            HttpStatusCode.InternalServerError, "Error");

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        var singleResult = results.ShouldHaveSingleItem();
        singleResult.Title.ShouldBe("Title 1");
    }

    [Fact]
    public async Task Search_WithEmptyResults_ReturnsEmptyArray()
    {
        // given
        SetupCompleteSearchScenario(["indexer1"], []);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_WithNoResultsProperty_ReturnsEmptyArray()
    {
        // given
        SetupIndexersResponse(["indexer1"]);
        const string invalidResponse = "{\"OtherProperty\": []}";
        SetupMockResponse(
            $"indexers/indexer1/results/?apikey={ApiKey}&Query={DefaultQuery}", HttpStatusCode.OK, invalidResponse);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_WithLowSeeders_FiltersOutResults()
    {
        // given
        var mixedResults = new[]
        {
            CreateTestResult("High Seeders", "movie", $"{LinkBaseUri}/high", 1000, 10, 5),
            CreateTestResult("Low Seeders", "movie", $"{LinkBaseUri}/low", 1000, 1, 5)
        };

        SetupCompleteSearchScenario(["indexer1"], mixedResults);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        var singleResult = results.ShouldHaveSingleItem();
        singleResult.Title.ShouldBe("High Seeders");
    }

    [Fact]
    public async Task Search_WithAlternativeIndexerFormat_ReturnsResults()
    {
        // given
        const string indexersXml = """
                                   <indexers>
                                       <indexer><id>alt-indexer</id></indexer>
                                   </indexers>
                                   """;
        SetupMockResponse(IndexersPath, HttpStatusCode.OK, indexersXml);

        var result = CreateTestResult("Alt Title", "movie", $"{LinkBaseUri}/alt", 1000, 10, 5);
        SetupIndexerSearchResponse("alt-indexer", [result]);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        var singleResult = results.ShouldHaveSingleItem();
        singleResult.Title.ShouldBe("Alt Title");
    }

    [Fact]
    public async Task Search_WithMagnetUriInsteadOfLink_ReturnsResults()
    {
        // given
        SetupIndexersResponse(["indexer1"]);

        var result = new JsonObject
        {
            ["Title"] = "Magnet Title",
            ["CategoryDesc"] = "movie",
            ["MagnetUri"] = "magnet:?xt=urn:btih:123456",
            ["Size"] = 1000,
            ["Seeders"] = 10.5,
            ["Peers"] = 5
        };

        var response = CreateJackettResponse([result]);
        SetupMockResponse($"indexers/indexer1/results/?apikey={ApiKey}&Query={DefaultQuery}",
            HttpStatusCode.OK, response);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        var singleResult = results.ShouldHaveSingleItem();
        singleResult.Title.ShouldBe("Magnet Title");
        singleResult.Link.ShouldBe("magnet:?xt=urn:btih:123456");
    }

    [Fact]
    public async Task Search_WithMoreThanMaxResults_ReturnsLimitedAndSortedResults()
    {
        // given
        SetupIndexersResponse(["indexer1"]);
        var results = Enumerable.Range(1, 15)
            .Select(i => CreateTestResult($"Title {i}", "movie", $"{LinkBaseUri}/{i}", 1000, i * 2, i))
            .ToArray();

        SetupIndexerSearchResponse("indexer1", results);

        // when
        var searchResults = await _client.Search(DefaultQuery);

        // then
        searchResults.Length.ShouldBe(10);
        searchResults[0].Title.ShouldBe("Title 15");
        searchResults[0].Seeders.ShouldBe(30);
        searchResults[9].Title.ShouldBe("Title 6");
        searchResults[9].Seeders.ShouldBe(12);
        searchResults.Select(x => x.Seeders).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task Search_WithSpecialCharactersInQuery_EncodesQueryCorrectly()
    {
        // given
        const string query = "test query & special chars";
        var encodedQuery = HttpUtility.UrlEncode(query);

        SetupIndexersResponse(["indexer1"]);

        var result = CreateTestResult("Encoded Title", "movie", $"{LinkBaseUri}/encoded", 1000, 10, 5);
        SetupMockResponse(
            $"indexers/indexer1/results/?apikey={ApiKey}&Query={encodedQuery}",
            HttpStatusCode.OK,
            CreateJackettResponse([result]));

        // when
        var results = await _client.Search(query);

        // then
        results.ShouldHaveSingleItem().Title.ShouldBe("Encoded Title");
    }

    [Fact]
    public async Task Search_WithDoubleNumericValues_ParsesCorrectly()
    {
        // given
        SetupIndexersResponse(["indexer1"]);

        var resultWithDoubles = new JsonObject
        {
            ["Title"] = "Double Values",
            ["CategoryDesc"] = "tv",
            ["Link"] = $"{LinkBaseUri}/double",
            ["Size"] = 1234.56,
            ["Seeders"] = 25.0,
            ["Peers"] = 10.9
        };

        SetupIndexerSearchResponse("indexer1", [resultWithDoubles]);

        // when
        var results = await _client.Search(DefaultQuery);

        // then
        var result = results.ShouldHaveSingleItem();
        result.Title.ShouldBe("Double Values");
        result.Size.ShouldBe(1234L);
        result.Seeders.ShouldBe(25L);
        result.Peers.ShouldBe(10L);
    }

    #region Helper Methods

    private void SetupIndexersResponse(string[] indexerIds)
    {
        var indexersXml = "<indexers>\n" +
                          string.Join("\n", indexerIds.Select(id => $"    <indexer id=\"{id}\"></indexer>")) +
                          "\n</indexers>";

        SetupMockResponse(IndexersPath, HttpStatusCode.OK, indexersXml);
    }

    private void SetupIndexerSearchResponse(string indexerId, JsonObject[] results)
    {
        var response = CreateJackettResponse(results);
        SetupMockResponse(
            $"indexers/{indexerId}/results/?apikey={ApiKey}&Query={DefaultQuery}", HttpStatusCode.OK, response);
    }

    private void SetupCompleteSearchScenario(string[] indexerIds, JsonObject[] resultsPerIndexer)
    {
        SetupIndexersResponse(indexerIds);

        foreach (var indexerId in indexerIds)
        {
            SetupIndexerSearchResponse(indexerId, resultsPerIndexer);
        }
    }

    private void SetupMockResponse(string relativeUri, HttpStatusCode statusCode, string content)
    {
        var absoluteUri = new Uri(_httpClient.BaseAddress!, relativeUri);

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Get &&
                    req.RequestUri != null &&
                    req.RequestUri.AbsoluteUri == absoluteUri.AbsoluteUri),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content, Encoding.UTF8,
                    content.StartsWith('<') ? "application/xml" : "application/json")
            });
    }

    private static string CreateJackettResponse(JsonObject[] results)
    {
        return JsonSerializer.Serialize(new
        {
            Results = results
        });
    }

    private static JsonObject CreateTestResult(
        string title, string category, string link, long size, int seeders, int peers)
    {
        return new JsonObject
        {
            ["Title"] = title,
            ["CategoryDesc"] = category,
            ["Link"] = link,
            ["Size"] = size,
            ["Seeders"] = seeders,
            ["Peers"] = peers
        };
    }

    #endregion
}