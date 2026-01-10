namespace Domain.DTOs;

public record WebPageMetadata(
    string? Description,
    string? Author,
    DateOnly? DatePublished,
    string? SiteName);

public record ExtractedLink(string Text, string Url);

public enum WebFetchOutputFormat
{
    Markdown,
    Text,
    Html
}

public enum WaitStrategy
{
    NetworkIdle,
    DomContentLoaded,
    Load,
    Selector,
    Stable
}

public record HtmlProcessingResult(
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    IReadOnlyList<ExtractedLink>? Links,
    bool IsPartial,
    string? ErrorMessage);