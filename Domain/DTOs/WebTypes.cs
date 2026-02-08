namespace Domain.DTOs;

public record ExtractedLink(string Text, string Url);

public enum WebFetchOutputFormat
{
    Markdown,
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