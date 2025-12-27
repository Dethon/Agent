using Domain.Contracts;

namespace Infrastructure.Clients;

public class WebContentFetcher(HttpClient httpClient, IHtmlProcessor htmlProcessor) : IWebFetcher
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public async Task<WebFetchResult> FetchAsync(WebFetchRequest request, CancellationToken ct = default)
    {
        if (!ValidateUrl(request.Url))
        {
            return CreateErrorResult(request.Url, "Invalid URL. Only http and https URLs are supported.");
        }

        try
        {
            var html = await PerformRequestAsync(request.Url, ct);
            return await htmlProcessor.ProcessAsync(request, html, ct);
        }
        catch (HttpRequestException ex)
        {
            return CreateErrorResult(request.Url, $"Network error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return CreateErrorResult(request.Url, "Request timed out");
        }
        catch (Exception ex)
        {
            return CreateErrorResult(request.Url, $"Error: {ex.Message}");
        }
    }

    private static bool ValidateUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
    }

    private async Task<string> PerformRequestAsync(string url, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        httpRequest.Headers.Add("User-Agent", UserAgent);
        httpRequest.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var response = await httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }

    private static WebFetchResult CreateErrorResult(string url, string message)
    {
        return new WebFetchResult(
            Url: url,
            Status: WebFetchStatus.Error,
            Title: null,
            Content: null,
            ContentLength: 0,
            Truncated: false,
            Metadata: null,
            Links: null,
            ErrorMessage: message
        );
    }
}