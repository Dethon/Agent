namespace Domain.Contracts;

public interface IWebBrowser
{
    Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default);
    Task<ClickResult> ClickAsync(ClickRequest request, CancellationToken ct = default);
    Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default);
    Task CloseSessionAsync(string sessionId, CancellationToken ct = default);
}

public record BrowseRequest(
    string SessionId,
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