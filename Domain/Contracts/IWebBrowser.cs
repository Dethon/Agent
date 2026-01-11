using Domain.DTOs;

namespace Domain.Contracts;

public interface IWebBrowser
{
    Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default);
    Task<ClickResult> ClickAsync(ClickRequest request, CancellationToken ct = default);
    Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default);
    Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken ct = default);
    Task CloseSessionAsync(string sessionId, CancellationToken ct = default);
}

public record InspectRequest(
    string SessionId,
    InspectMode Mode,
    string? Query = null,
    bool Regex = false,
    int MaxResults = 20,
    string? Selector = null);

public enum InspectMode
{
    Structure,
    Search,
    Forms,
    Interactive
}

public record InspectResult(
    string SessionId,
    string? Url,
    string? Title,
    InspectMode Mode,
    InspectStructure? Structure,
    InspectSearchResult? SearchResult,
    IReadOnlyList<InspectForm>? Forms,
    InspectInteractive? Interactive,
    string? ErrorMessage);

public record InspectStructure(
    ContentRegion? MainContent,
    IReadOnlyList<RepeatingElements> RepeatingElements,
    NavigationInfo? Navigation,
    IReadOnlyList<OutlineNode> Outline,
    IReadOnlyList<string> Suggestions,
    int TotalTextLength);

public record ContentRegion(
    string Selector,
    string? Preview,
    int TextLength);

public record RepeatingElements(
    string Selector,
    int Count,
    string? Preview,
    IReadOnlyList<string>? DetectedFields);

public record NavigationInfo(
    string? PaginationSelector,
    string? NextPageSelector,
    string? PrevPageSelector,
    string? MenuSelector);

public record OutlineNode(
    string Tag,
    string Selector,
    string? Preview,
    int TextLength,
    IReadOnlyList<OutlineNode>? Children);

public record InspectSearchResult(
    string Query,
    int TotalMatches,
    IReadOnlyList<InspectSearchMatch> Matches);

public record InspectSearchMatch(string Text, string Context, string NearestSelector, string? NearestHeading);

public record InspectForm(
    string? Name,
    string? Action,
    string? Method,
    string Selector,
    IReadOnlyList<InspectFormField> Fields,
    IReadOnlyList<InspectButton> Buttons);

public record InspectFormField(
    string Type,
    string? Name,
    string? Label,
    string? Placeholder,
    string Selector,
    bool Required);

public record InspectButton(string Tag, string? Text, string Selector, int Count = 1);

public record InspectInteractive(
    IReadOnlyList<InspectButton> Buttons,
    IReadOnlyList<InspectLink> Links);

public record InspectLink(string? Text, string Selector, int Count = 1);

public record BrowseRequest(
    string SessionId,
    string Url,
    string? Selector = null,
    WebFetchOutputFormat Format = WebFetchOutputFormat.Markdown,
    int MaxLength = 10000,
    int Offset = 0,
    bool IncludeLinks = true,
    bool UseReadability = false,
    WaitStrategy WaitStrategy = WaitStrategy.NetworkIdle,
    string? WaitSelector = null,
    int WaitTimeoutMs = 30000,
    int ExtraDelayMs = 1000,
    bool ScrollToLoad = false,
    int ScrollSteps = 3,
    bool WaitForStability = false,
    int StabilityCheckMs = 500,
    bool DismissModals = true,
    ModalDismissalConfig? ModalConfig = null);

public record BrowseResult(
    string SessionId,
    string Url,
    BrowseStatus Status,
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    IReadOnlyList<ExtractedLink>? Links,
    IReadOnlyList<ModalDismissed>? DismissedModals,
    string? ErrorMessage);

public record WebPageMetadata(
    string? Description,
    string? Author,
    DateOnly? DatePublished,
    string? SiteName);

public record ClickRequest(
    string SessionId,
    string Selector,
    ClickAction Action = ClickAction.Click,
    string? Text = null,
    bool WaitForNavigation = false,
    int WaitTimeoutMs = 30000);

public record ClickResult(
    string SessionId,
    ClickStatus Status,
    string? CurrentUrl,
    string? Content,
    int ContentLength,
    string? ErrorMessage,
    bool NavigationOccurred);

public enum BrowseStatus
{
    Success,
    Partial,
    Error,
    SessionNotFound,
    CaptchaRequired
}

public enum ClickStatus
{
    Success,
    Error,
    ElementNotFound,
    SessionNotFound,
    Timeout
}

public enum ClickAction
{
    Click,
    DoubleClick,
    RightClick,
    Hover
}

public record ModalDismissed(ModalType Type, string Selector, string? ButtonText);