using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Domain.Contracts;
using SmartReader;

namespace Infrastructure.Clients;

public partial class WebContentFetcher(HttpClient httpClient) : IWebFetcher
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public async Task<WebFetchResult> FetchAsync(WebFetchRequest request, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return CreateErrorResult(request.Url, "Invalid URL. Only http and https URLs are supported.");
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
            httpRequest.Headers.Add("User-Agent", UserAgent);
            httpRequest.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

            var response = await httpClient.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                return CreateErrorResult(request.Url, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            return await ProcessHtmlAsync(request, html, ct);
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

    private static async Task<WebFetchResult> ProcessHtmlAsync(
        WebFetchRequest request, string html, CancellationToken ct)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(html), ct);

        var title = document.Title;
        var metadata = ExtractMetadata(document);
        string content;
        List<ExtractedLink>? links = null;

        if (!string.IsNullOrEmpty(request.Selector))
        {
            var element = document.QuerySelector(request.Selector);
            if (element == null)
            {
                return new WebFetchResult(
                    Url: request.Url,
                    Status: WebFetchStatus.Partial,
                    Title: title,
                    Content: "Selector did not match any elements",
                    ContentLength: 0,
                    Truncated: false,
                    Metadata: metadata,
                    Links: null,
                    ErrorMessage: $"CSS selector '{request.Selector}' did not match any elements"
                );
            }

            content = FormatContent(element, request.Format);
            if (request.IncludeLinks)
            {
                links = ExtractLinks(element);
            }
        }
        else
        {
            var reader = new Reader(request.Url, html);
            var article = await reader.GetArticleAsync(ct);

            if (string.IsNullOrEmpty(article.Content))
            {
                content = FormatContent(document.Body ?? document.DocumentElement, request.Format);
            }
            else
            {
                title = article.Title;

                if (article.PublicationDate.HasValue)
                {
                    metadata = metadata with
                    {
                        DatePublished = DateOnly.FromDateTime(article.PublicationDate.Value)
                    };
                }

                if (!string.IsNullOrEmpty(article.Author))
                {
                    metadata = metadata with { Author = article.Author };
                }

                if (!string.IsNullOrEmpty(article.SiteName))
                {
                    metadata = metadata with { SiteName = article.SiteName };
                }

                content = request.Format switch
                {
                    WebFetchOutputFormat.Html => article.Content,
                    WebFetchOutputFormat.Text => HtmlToText(article.Content),
                    _ => HtmlToMarkdown(article.Content)
                };
            }

            if (request.IncludeLinks && document.Body != null)
            {
                links = ExtractLinks(document.Body);
            }
        }

        var truncated = content.Length > request.MaxLength;
        if (truncated)
        {
            content = TruncateAtBoundary(content, request.MaxLength);
        }

        return new WebFetchResult(
            Url: request.Url,
            Status: WebFetchStatus.Success,
            Title: title,
            Content: content,
            ContentLength: content.Length,
            Truncated: truncated,
            Metadata: metadata,
            Links: links,
            ErrorMessage: null
        );
    }

    private static WebPageMetadata ExtractMetadata(IDocument document)
    {
        string? description = null;
        string? author = null;
        DateOnly? datePublished = null;
        string? siteName = null;

        var metaTags = document.QuerySelectorAll("meta");
        foreach (var meta in metaTags)
        {
            var name = meta.GetAttribute("name")?.ToLowerInvariant();
            var property = meta.GetAttribute("property")?.ToLowerInvariant();
            var content = meta.GetAttribute("content");

            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            if (name == "description" || property == "og:description")
            {
                description ??= content;
            }
            else if (name == "author")
            {
                author ??= content;
            }
            else if (property == "og:site_name")
            {
                siteName ??= content;
            }
            else if (property == "article:published_time" || name == "date")
            {
                if (DateTime.TryParse(content, out var date))
                {
                    datePublished ??= DateOnly.FromDateTime(date);
                }
            }
        }

        return new WebPageMetadata(description, author, datePublished, siteName);
    }

    private static List<ExtractedLink> ExtractLinks(IElement element)
    {
        var links = new List<ExtractedLink>();
        var anchors = element.QuerySelectorAll("a[href]");

        foreach (var anchor in anchors)
        {
            var href = anchor.GetAttribute("href");
            var text = anchor.TextContent.Trim();

            if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(text) || !href.StartsWith("http"))
            {
                continue;
            }

            if (text.Length > 100)
            {
                text = text[..97] + "...";
            }

            links.Add(new ExtractedLink(text, href));
        }

        return links.Take(50).ToList();
    }

    private static string FormatContent(IElement? element, WebFetchOutputFormat format)
    {
        if (element == null)
        {
            return string.Empty;
        }

        return format switch
        {
            WebFetchOutputFormat.Html => element.InnerHtml,
            WebFetchOutputFormat.Text => element.TextContent,
            _ => HtmlToMarkdown(element.InnerHtml)
        };
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = html;
        text = BrTagRegex().Replace(text, "\n");
        text = ParagraphTagRegex().Replace(text, "\n\n");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = MultipleNewlinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    private static string HtmlToMarkdown(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var md = html;

        md = Regex.Replace(md, "<h1[^>]*>(.*?)</h1>", "# $1\n\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<h2[^>]*>(.*?)</h2>", "## $1\n\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<h3[^>]*>(.*?)</h3>", "### $1\n\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<h4[^>]*>(.*?)</h4>", "#### $1\n\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<h5[^>]*>(.*?)</h5>", "##### $1\n\n",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<h6[^>]*>(.*?)</h6>", "###### $1\n\n",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        md = Regex.Replace(md, "<strong[^>]*>(.*?)</strong>", "**$1**",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<b[^>]*>(.*?)</b>", "**$1**", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<em[^>]*>(.*?)</em>", "*$1*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<i[^>]*>(.*?)</i>", "*$1*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<code[^>]*>(.*?)</code>", "`$1`", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        md = Regex.Replace(md, """<a\s+href=["']([^"']+)["'][^>]*>(.*?)</a>""", "[$2]($1)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        md = Regex.Replace(md, "<li[^>]*>(.*?)</li>", "- $1\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<[uo]l[^>]*>", "\n", RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "</[uo]l>", "\n", RegexOptions.IgnoreCase);

        md = Regex.Replace(md, "<pre[^>]*><code[^>]*>(.*?)</code></pre>", "\n```\n$1\n```\n",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        md = Regex.Replace(md, "<pre[^>]*>(.*?)</pre>", "\n```\n$1\n```\n",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        md = BrTagRegex().Replace(md, "\n");
        md = ParagraphTagRegex().Replace(md, "\n\n");
        md = HtmlTagRegex().Replace(md, string.Empty);

        md = WebUtility.HtmlDecode(md);
        md = MultipleNewlinesRegex().Replace(md, "\n\n");
        md = MultipleSpacesRegex().Replace(md, " ");

        return md.Trim();
    }

    private static string TruncateAtBoundary(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var truncated = text[..(maxLength - 20)];

        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > maxLength * 0.7)
        {
            truncated = truncated[..lastNewline];
        }

        return truncated + "\n\n[Content truncated...]";
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

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    [GeneratedRegex("</?(p|div)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphTagRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlinesRegex();

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpacesRegex();
}