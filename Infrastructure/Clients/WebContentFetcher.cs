using Domain.Contracts;
using Infrastructure.HtmlProcessing;

namespace Infrastructure.Clients;

public class WebContentFetcher(HttpClient httpClient) : IWebFetcher
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
            return await HtmlProcessor.ProcessAsync(request, html, ct);
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
        httpRequest.Headers.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        httpRequest.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        httpRequest.Headers.Add("Sec-Ch-Ua",
            "\"Not_A Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\"");
        httpRequest.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        httpRequest.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        httpRequest.Headers.Add("Sec-Fetch-Dest", "document");
        httpRequest.Headers.Add("Sec-Fetch-Mode", "navigate");
        httpRequest.Headers.Add("Sec-Fetch-Site", "none");
        httpRequest.Headers.Add("Sec-Fetch-User", "?1");
        httpRequest.Headers.Add("Upgrade-Insecure-Requests", "1");

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