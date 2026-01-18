using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;

namespace Infrastructure.Clients.Browser;

public class CapSolverClient(HttpClient httpClient, string apiKey) : ICaptchaSolver
{
    private const string CreateTaskUrl = "https://api.capsolver.com/createTask";
    private const string GetTaskResultUrl = "https://api.capsolver.com/getTaskResult";
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _maxWaitTime = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<CaptchaSolution> SolveDataDomeAsync(DataDomeCaptchaRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var taskId = await CreateTaskAsync(request, ct);
            if (taskId == null)
            {
                return new CaptchaSolution(false, null, "Failed to create CAPTCHA task");
            }

            return await WaitForSolutionAsync(taskId, ct);
        }
        catch (Exception ex)
        {
            return new CaptchaSolution(false, null, $"CAPTCHA solving error: {ex.Message}");
        }
    }

    private async Task<string?> CreateTaskAsync(DataDomeCaptchaRequest request, CancellationToken ct)
    {
        var payload = new
        {
            clientKey = apiKey,
            task = new
            {
                type = "DataDomeSliderTask",
                websiteURL = request.WebsiteUrl,
                captchaUrl = request.CaptchaUrl,
                userAgent = request.UserAgent
            }
        };

        var response = await httpClient.PostAsJsonAsync(CreateTaskUrl, payload, _jsonOptions, ct);
        var result = await response.Content.ReadFromJsonAsync<CreateTaskResponse>(_jsonOptions, ct);

        return result?.ErrorId != 0 ? null : result.TaskId;
    }

    private async Task<CaptchaSolution> WaitForSolutionAsync(string taskId, CancellationToken ct)
    {
        var elapsed = TimeSpan.Zero;

        while (elapsed < _maxWaitTime)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(_pollInterval, ct);
            elapsed += _pollInterval;

            var payload = new
            {
                clientKey = apiKey,
                taskId
            };

            var response = await httpClient.PostAsJsonAsync(GetTaskResultUrl, payload, _jsonOptions, ct);
            var result = await response.Content.ReadFromJsonAsync<GetTaskResultResponse>(_jsonOptions, ct);

            if (result == null)
            {
                continue;
            }

            if (result.ErrorId != 0)
            {
                return new CaptchaSolution(false, null, result.ErrorDescription ?? "Unknown error");
            }

            if (result is { Status: "ready", Solution: not null })
            {
                return new CaptchaSolution(true, result.Solution.Cookie, null);
            }
        }

        return new CaptchaSolution(false, null, "Timeout waiting for CAPTCHA solution");
    }

    private record CreateTaskResponse(
        [property: JsonPropertyName("errorId")]
        int ErrorId,
        [property: JsonPropertyName("errorDescription")]
        string? ErrorDescription,
        [property: JsonPropertyName("taskId")] string? TaskId);

    private record GetTaskResultResponse(
        [property: JsonPropertyName("errorId")]
        int ErrorId,
        [property: JsonPropertyName("errorDescription")]
        string? ErrorDescription,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("solution")]
        SolutionData? Solution);

    private record SolutionData(
        [property: JsonPropertyName("cookie")] string? Cookie);
}