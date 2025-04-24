using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using System.Xml.Linq;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients;

public class JackettSearchClient(HttpClient client, string apiKey) : ISearchClient
{
    public async Task<SearchResult[]> Search(string query, CancellationToken cancellationToken = default)
    {
        var indexers = await GetIndexers(cancellationToken);
        var tasks = indexers.Select(x => QueryIndexer(x, query, cancellationToken));
        return (await Task.WhenAll(tasks)).SelectMany(x => x).ToArray();
    }

    private async Task<string[]> GetIndexers(CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = $"indexers/all/results/torznab/api?apikey={apiKey}&t=indexers&configured=true";
            var response = await client.GetStringAsync(requestUri, cancellationToken);
            var xmlDoc = XDocument.Parse(response);

            var indexerIds = xmlDoc.Descendants("indexer")
                .Select(x => x.Attribute("id")?.Value ?? x.Element("id")?.Value)
                .Where(x => x is not null)
                .Cast<string>()
                .ToArray();

            return indexerIds.Length > 0 ? indexerIds : ["all"];
        }
        catch
        {
            return ["all"];
        }
    }

    private async Task<SearchResult[]> QueryIndexer(string indexer, string searchQuery,
        CancellationToken cancellationToken)
    {
        try
        {
            var encodedQuery = HttpUtility.UrlEncode(searchQuery);
            var requestUri = $"indexers/{indexer}/results/?apikey={apiKey}&Query={encodedQuery}";
            var response = await client.GetAsync(requestUri, cancellationToken);
            return !response.IsSuccessStatusCode
                ? []
                : ParseSingleResponse(await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken));
        }
        catch
        {
            return [];
        }
    }

    private static SearchResult[] ParseSingleResponse(JsonDocument? jackettResponse)
    {
        if (jackettResponse?.RootElement is null)
        {
            return [];
        }

        var existingResults = jackettResponse.RootElement.TryGetProperty("Results", out var jackettResults);
        if (!existingResults)
        {
            return [];
        }

        var results = jackettResults.EnumerateArray()
            .Select(x => x.Deserialize<JsonNode>())
            .Where(x => x is not null)
            .Cast<JsonNode>()
            .ToArray();
        return TrimSingleResultSet(results);
    }

    private static SearchResult[] TrimSingleResultSet(IEnumerable<JsonNode> allResults, int maxResults = 10)
    {
        var trimmedResults = allResults
            .Where(x => ForceGetInt(x["Seeders"]) > 1)
            .OrderByDescending(x => ForceGetInt(x["Seeders"]))
            .Take(maxResults)
            .Select(x =>
            {
                try
                {
                    var link = x["Link"]?.GetValue<string>() ?? x["MagnetUri"]?.GetValue<string>() ?? string.Empty;
                    return new SearchResult
                    {
                        Title = x["Title"]?.GetValue<string>() ?? string.Empty,
                        Category = x["CategoryDesc"]?.GetValue<string>(),
                        Id = link.GetHashCode(),
                        Link = link,
                        Size = ForceGetInt(x["Size"]),
                        Seeders = ForceGetInt(x["Seeders"]),
                        Peers = ForceGetInt(x["Peers"])
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(x => x is not null && x.Link != string.Empty)
            .Cast<SearchResult>()
            .ToArray();

        return trimmedResults;
    }

    private static long? ForceGetInt(JsonNode? jsonNode)
    {
        return (long?)jsonNode?.GetValue<double?>();
    }
}