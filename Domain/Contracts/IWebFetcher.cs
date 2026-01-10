namespace Domain.Contracts;

public interface IWebFetcher
{
    Task<WebFetchResult> FetchAsync(WebFetchRequest request, CancellationToken ct = default);
    Task<WebFetchResult> ResolveCaptchaAsync(WebFetchRequest request, CancellationToken ct = default);
}

public record WebFetchRequest(
    string Url,
    string? Selector = null,
    WebFetchOutputFormat Format = WebFetchOutputFormat.Markdown,
    int MaxLength = 10000,
    bool IncludeLinks = true,
    WaitStrategy WaitStrategy = WaitStrategy.NetworkIdle,
    string? WaitSelector = null,
    int WaitTimeoutMs = 30000,
    int ExtraDelayMs = 1000,
    bool ScrollToLoad = false,
    int ScrollSteps = 3,
    bool WaitForStability = false,
    int StabilityCheckMs = 500);

public enum WaitStrategy
{
    NetworkIdle,
    DomContentLoaded,
    Load,
    Selector,
    Stable
}

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
    Error,
    CaptchaRequired
}