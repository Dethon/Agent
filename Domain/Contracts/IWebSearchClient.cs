namespace Domain.Contracts;

public interface IWebSearchClient
{
    Task<WebSearchResult> SearchAsync(WebSearchQuery query, CancellationToken ct = default);
}

public record WebSearchQuery(
    string Query,
    int MaxResults = 10,
    string? Site = null,
    DateRange? DateRange = null);

public record WebSearchResult(
    string Query,
    long TotalResults,
    IReadOnlyList<WebSearchResultItem> Results,
    string SearchEngine,
    double SearchTimeSeconds);

public record WebSearchResultItem(
    string Title,
    string Url,
    string Snippet,
    string Domain,
    DateOnly? DatePublished);

public enum DateRange
{
    Day,
    Week,
    Month,
    Year
}