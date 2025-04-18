using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Domain.Tools;

namespace Infrastructure.ToolAdapters.FileSearchTools;

public class JackettSearchAdapter(HttpClient client, string apiKey) : FileSearchTool
{
    protected override async Task<JsonNode> Resolve(FileSearchParams parameters, CancellationToken cancellationToken)
    {
        var baseUrl = $"indexers/all/results/?apikey={apiKey}";
        var requestUri = $"{baseUrl}&Query={HttpUtility.UrlEncode(parameters.SearchString)}";
        var response = await client.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var jackettResponse = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
        return GetResults(jackettResponse);
    }

    private static JsonObject GetResults(JsonDocument? jackettResponse)
    {
        if (jackettResponse is null)
            throw new ArgumentNullException(nameof(jackettResponse), "Jackett response cannot be null");

        var existingResults = jackettResponse.RootElement.TryGetProperty("Results", out var jackettResults);
        var results = jackettResults.EnumerateArray().AsEnumerable().ToArray();
        if (!existingResults || results.Length == 0)
            return new JsonObject
            {
                ["status"] = "noResults",
                ["message"] = "File search completed successfully but returned no results",
                ["totalResults"] = 0
            };

        var trimmedResults = TrimResponseForLlm(results);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File search completed successfully",
            ["totalResults"] = trimmedResults.Count,
            ["results"] = trimmedResults
        };
    }

    private static JsonArray TrimResponseForLlm(IEnumerable<JsonElement> allResults, int maxResults = 200)
    {
        var trimmedResults = allResults
            .Where(x => x.GetProperty("Seeders").GetInt32() > 0)
            .OrderByDescending(x => x.GetProperty("Seeders").GetInt32())
            .Take(maxResults)
            .Select(x => new JsonObject
            {
                ["Title"] = x.GetProperty("Title").GetString(),
                ["Category"] = x.GetProperty("CategoryDesc").GetString(),
                ["Link"] = x.GetProperty("Link").GetString(),
                ["PublishDate"] = x.GetProperty("PublishDate").GetString(),
                ["Size"] = ForceGetInt(x.GetProperty("Size")),
                ["Seeders"] = ForceGetInt(x.GetProperty("Seeders")),
                ["Peers"] = ForceGetInt(x.GetProperty("Peers"))
            })
            .Cast<JsonNode>()
            .ToArray();

        return new JsonArray(trimmedResults);
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