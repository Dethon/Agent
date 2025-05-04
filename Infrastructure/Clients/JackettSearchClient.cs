using System.Web;
using System.Xml.Linq;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients;

public class JackettSearchClient(HttpClient client, string apiKey, Dictionary<string, string> mappings) : ISearchClient
{
    private readonly XNamespace _torznabNs = "http://torznab.com/schemas/2015/feed";

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

    private async Task<SearchResult[]> QueryIndexer(
        string indexer, string searchQuery, CancellationToken cancellationToken)
    {
        try
        {
            var encodedQuery = HttpUtility.UrlEncode(searchQuery);
            var requestUri = $"indexers/{indexer}/results/torznab/api?apikey={apiKey}&t=search&q={encodedQuery}";
            var response = await client.GetAsync(requestUri, cancellationToken);

            return !response.IsSuccessStatusCode
                ? []
                : ParseTorznabResponse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch
        {
            return [];
        }
    }

    private SearchResult[] ParseTorznabResponse(string xmlContent)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
        {
            return [];
        }

        try
        {
            var xmlDoc = XDocument.Parse(xmlContent);
            var items = xmlDoc.Descendants("item").ToArray();
            return TrimAndMapResults(items, ParseTorznabItem);
        }
        catch
        {
            return [];
        }
    }

    private SearchResult? ParseTorznabItem(XElement item)
    {
        try
        {
            var titleElement = item.Element("title");
            var linkElement = item.Element("link");
            var sizeElement = item.Element("size");
            var torznabAttributes = item.Elements(_torznabNs + "attr")
                .GroupBy(
                    attr => attr.Attribute("name")?.Value ?? string.Empty,
                    attr => attr.Attribute("value")?.Value)
                .ToDictionary(x => x.Key, x => string.Join("/", x));

            var link = torznabAttributes.GetValueOrDefault("magneturl") ?? linkElement?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(link))
            {
                return null;
            }

            var title = ApplyMappings(titleElement?.Value ?? string.Empty);
            var category = ApplyMappings(torznabAttributes.GetValueOrDefault("category") ?? string.Empty);
            long? size = null;
            int? seeders = null;
            int? peers = null;
            if (long.TryParse(sizeElement?.Value, out var s))
            {
                size = s;
            }

            if (int.TryParse(torznabAttributes.GetValueOrDefault("seeders"), out var sd))
            {
                seeders = sd;
            }

            if (int.TryParse(torznabAttributes.GetValueOrDefault("peers"), out var p))
            {
                peers = p;
            }

            return new SearchResult
            {
                Title = title,
                Category = category,
                Id = link.GetHashCode(),
                Link = link,
                Size = size,
                Seeders = seeders,
                Peers = peers
            };
        }
        catch
        {
            return null;
        }
    }

    private static SearchResult[] TrimAndMapResults<T>(
        IEnumerable<T> sourceItems, Func<T, SearchResult?> parser, int maxResults = 10)
    {
        return sourceItems
            .Select(parser)
            .Where(x => x is not null && !string.IsNullOrEmpty(x.Link) && x.Seeders.GetValueOrDefault(0) > 1)
            .OrderByDescending(x => x.Seeders.GetValueOrDefault(0))
            .Take(maxResults)
            .ToArray();
    }

    private string ApplyMappings(string value)
    {
        foreach (var mapping in mappings)
        {
            const StringComparison comparisonType = StringComparison.InvariantCultureIgnoreCase;
            if (value.Contains(mapping.Key, comparisonType))
            {
                value = value.Replace(mapping.Key, mapping.Value, comparisonType);
            }
        }

        return value;
    }
}