namespace Domain.Contracts;

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