namespace Domain.Contracts;

public interface ICaptchaSolver
{
    Task<CaptchaSolution> SolveDataDomeAsync(DataDomeCaptchaRequest request, CancellationToken ct = default);
}

public record DataDomeCaptchaRequest(
    string WebsiteUrl,
    string CaptchaUrl,
    string UserAgent);

public record CaptchaSolution(
    bool Success,
    string? Cookie,
    string? ErrorMessage);