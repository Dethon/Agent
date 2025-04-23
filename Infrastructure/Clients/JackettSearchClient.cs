using System.Net.Http.Json;
using System.Text.Json;
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

        var results = jackettResults.EnumerateArray().AsEnumerable().ToArray();
        return TrimSingleResultSet(results);
    }

    private static SearchResult[] TrimSingleResultSet(IEnumerable<JsonElement> allResults, int maxResults = 10)
    {
        var trimmedResults = allResults
            .Where(x => x.GetProperty("Seeders").GetInt32() > 1)
            .OrderByDescending(x => x.GetProperty("Seeders").GetInt32())
            .Take(maxResults)
            .Select(x =>
            {
                var link = x.GetProperty("Link").GetString() ?? x.GetProperty("MagnetUri").GetString() ?? string.Empty;
                return new SearchResult
                {
                    Title = x.GetProperty("Title").GetString() ?? string.Empty,
                    Category = x.GetProperty("CategoryDesc").GetString(),
                    Id = link.GetHashCode(),
                    Link = link,
                    Size = ForceGetInt(x.GetProperty("Size")),
                    Seeders = ForceGetInt(x.GetProperty("Seeders")),
                    Peers = ForceGetInt(x.GetProperty("Peers"))
                };
            })
            .ToArray();

        return trimmedResults;
    }

    private static long? ForceGetInt(JsonElement jsonElement)
    {
        if (jsonElement.ValueKind != JsonValueKind.Number) return null;

        var longSuccess = jsonElement.TryGetInt64(out var longResult);
        var doubleSuccess = jsonElement.TryGetDouble(out var doubleResult);
        if (longSuccess) return longResult;

        if (doubleSuccess) return (long)doubleResult;

        return null;
    }
}