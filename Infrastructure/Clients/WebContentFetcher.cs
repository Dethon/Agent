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
        if (!ValidateUrl(request.Url))
        {
            return CreateErrorResult(request.Url, "Invalid URL. Only http and https URLs are supported.");
        }

        try
        {
            var html = await PerformRequestAsync(request.Url, ct);
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

    private async Task<WebFetchResult> ProcessHtmlAsync(WebFetchRequest request, string html, CancellationToken ct)
    {
        var document = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        return !string.IsNullOrEmpty(request.Selector)
            ? ProcessWithSelector(request, document)
            : await ProcessWithSmartReaderAsync(request, html, document, ct);
    }

    private static WebFetchResult ProcessWithSelector(WebFetchRequest request, IDocument document)
    {
        var element = document.QuerySelector(request.Selector!);
        if (element == null)
        {
            return CreatePartialResult(request, document.Title, ExtractMetadata(document),
                $"CSS selector '{request.Selector}' did not match any elements");
        }

        var content = FormatContent(element, request.Format);
        var links = request.IncludeLinks ? ExtractLinks(element) : null;

        return CreateSuccessResult(request, document.Title, content, ExtractMetadata(document), links);
    }

    private static async Task<WebFetchResult> ProcessWithSmartReaderAsync(
        WebFetchRequest request, string html, IDocument document, CancellationToken ct)
    {
        var article = await new Reader(request.Url, html).GetArticleAsync(ct);

        if (string.IsNullOrEmpty(article.Content))
        {
            var content = FormatContent(document.Body ?? document.DocumentElement, request.Format);
            var links = request.IncludeLinks && document.Body != null ? ExtractLinks(document.Body) : null;
            return CreateSuccessResult(request, document.Title, content, ExtractMetadata(document), links);
        }

        var metadata = UpdateMetadataFromArticle(ExtractMetadata(document), article);
        var articleContent = FormatArticleContent(article, request.Format);
        var articleLinks = request.IncludeLinks && document.Body != null ? ExtractLinks(document.Body) : null;

        return CreateSuccessResult(request, article.Title, articleContent, metadata, articleLinks);
    }

    private static WebPageMetadata UpdateMetadataFromArticle(WebPageMetadata metadata, Article article)
    {
        return metadata with
        {
            DatePublished = article.PublicationDate.HasValue
                ? DateOnly.FromDateTime(article.PublicationDate.Value)
                : metadata.DatePublished,
            Author = !string.IsNullOrEmpty(article.Author) ? article.Author : metadata.Author,
            SiteName = !string.IsNullOrEmpty(article.SiteName) ? article.SiteName : metadata.SiteName
        };
    }

    private static string FormatArticleContent(Article article, WebFetchOutputFormat format)
    {
        return format switch
        {
            WebFetchOutputFormat.Html => article.Content,
            WebFetchOutputFormat.Text => HtmlToText(article.Content),
            _ => HtmlToMarkdown(article.Content)
        };
    }

    private static WebFetchResult CreateSuccessResult(
        WebFetchRequest request, string? title, string content, WebPageMetadata metadata, List<ExtractedLink>? links)
    {
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

    private static WebFetchResult CreatePartialResult(
        WebFetchRequest request, string? title, WebPageMetadata metadata, string errorMessage)
    {
        return new WebFetchResult(
            Url: request.Url,
            Status: WebFetchStatus.Partial,
            Title: title,
            Content: errorMessage,
            ContentLength: 0,
            Truncated: false,
            Metadata: metadata,
            Links: null,
            ErrorMessage: errorMessage
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
        md = ReplaceHeadings(md);
        md = ReplaceFormatting(md);
        md = ReplaceLinks(md);
        md = ReplaceLists(md);
        md = ReplaceCodeBlocks(md);
        md = CleanUpHtml(md);

        return md.Trim();
    }

    private static string ReplaceHeadings(string md)
    {
        md = H1TagRegex().Replace(md, "# $1\n\n");
        md = H2TagRegex().Replace(md, "## $1\n\n");
        md = H3TagRegex().Replace(md, "### $1\n\n");
        md = H4TagRegex().Replace(md, "#### $1\n\n");
        md = H5TagRegex().Replace(md, "##### $1\n\n");
        md = H6TagRegex().Replace(md, "###### $1\n\n");
        return md;
    }

    private static string ReplaceFormatting(string md)
    {
        md = StrongTagRegex().Replace(md, "**$1**");
        md = BoldTagRegex().Replace(md, "**$1**");
        md = EmTagRegex().Replace(md, "*$1*");
        md = ItalicTagRegex().Replace(md, "*$1*");
        md = CodeTagRegex().Replace(md, "`$1`");
        return md;
    }

    private static string ReplaceLinks(string md)
    {
        return AnchorTagRegex().Replace(md, "[$2]($1)");
    }

    private static string ReplaceLists(string md)
    {
        md = ListItemTagRegex().Replace(md, "- $1\n");
        md = ListOpenTagRegex().Replace(md, "\n");
        md = ListCloseTagRegex().Replace(md, "\n");
        return md;
    }

    private static string ReplaceCodeBlocks(string md)
    {
        md = PreCodeTagRegex().Replace(md, "\n```\n$1\n```\n");
        md = PreTagRegex().Replace(md, "\n```\n$1\n```\n");
        return md;
    }

    private static string CleanUpHtml(string md)
    {
        md = BrTagRegex().Replace(md, "\n");
        md = ParagraphTagRegex().Replace(md, "\n\n");
        md = HtmlTagRegex().Replace(md, string.Empty);

        md = WebUtility.HtmlDecode(md);
        md = MultipleNewlinesRegex().Replace(md, "\n\n");
        md = MultipleSpacesRegex().Replace(md, " ");
        return md;
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

    [GeneratedRegex("<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H1TagRegex();

    [GeneratedRegex("<h2[^>]*>(.*?)</h2>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H2TagRegex();

    [GeneratedRegex("<h3[^>]*>(.*?)</h3>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H3TagRegex();

    [GeneratedRegex("<h4[^>]*>(.*?)</h4>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H4TagRegex();

    [GeneratedRegex("<h5[^>]*>(.*?)</h5>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H5TagRegex();

    [GeneratedRegex("<h6[^>]*>(.*?)</h6>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex H6TagRegex();

    [GeneratedRegex("<strong[^>]*>(.*?)</strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StrongTagRegex();

    [GeneratedRegex("<b[^>]*>(.*?)</b>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BoldTagRegex();

    [GeneratedRegex("<em[^>]*>(.*?)</em>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex EmTagRegex();

    [GeneratedRegex("<i[^>]*>(.*?)</i>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ItalicTagRegex();

    [GeneratedRegex("<code[^>]*>(.*?)</code>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeTagRegex();

    [GeneratedRegex("""<a\s+href=["']([^"']+)["'][^>]*>(.*?)</a>""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnchorTagRegex();

    [GeneratedRegex("<li[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ListItemTagRegex();

    [GeneratedRegex("<[uo]l[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListOpenTagRegex();

    [GeneratedRegex("</[uo]l>", RegexOptions.IgnoreCase)]
    private static partial Regex ListCloseTagRegex();

    [GeneratedRegex("<pre[^>]*><code[^>]*>(.*?)</code></pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PreCodeTagRegex();

    [GeneratedRegex("<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PreTagRegex();
}