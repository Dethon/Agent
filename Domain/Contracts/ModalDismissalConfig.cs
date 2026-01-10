namespace Domain.Contracts;

public record ModalDismissalConfig(
    bool Enabled = true,
    IReadOnlyList<ModalPattern>? CustomPatterns = null,
    IReadOnlyList<ModalType>? DisabledTypes = null,
    int TimeoutMs = 3000);

public record ModalPattern(
    ModalType Type,
    string? ContainerSelector,
    IReadOnlyList<string> ButtonSelectors,
    IReadOnlyList<string>? ButtonTextPatterns = null);

public enum ModalType
{
    CookieConsent,
    AgeGate,
    Newsletter,
    Notification,
    Generic
}