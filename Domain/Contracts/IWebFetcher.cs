namespace Domain.Contracts;

public interface IWebFetcher
{
    Task<WebFetchResult> FetchAsync(WebFetchRequest request, CancellationToken ct = default);
}

public record WebFetchRequest(
    string Url,
    string? Selector = null,
    WebFetchOutputFormat Format = WebFetchOutputFormat.Markdown,
    int MaxLength = 10000,
    bool IncludeLinks = true);

public record WebFetchResult(
    string Url,
    WebFetchStatus Status,
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    IReadOnlyList<ExtractedLink>? Links,
    string? ErrorMessage);

public record WebPageMetadata(
    string? Description,
    string? Author,
    DateOnly? DatePublished,
    string? SiteName);

public record ExtractedLink(string Text, string Url);

public enum WebFetchOutputFormat
{
    Text,
    Markdown,
    Html
}

public enum WebFetchStatus
{
    Success,
    Partial,
    Error
}