using AngleSharp;
using AngleSharp.Dom;
using Domain.Contracts;
using SmartReader;

namespace Infrastructure.HtmlProcessing;

public static class HtmlProcessor
{
    public static async Task<WebFetchResult> ProcessAsync(WebFetchRequest request, string html, CancellationToken ct)
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

        var content = HtmlConverter.Convert(element, request.Format);
        var links = request.IncludeLinks ? ExtractLinks(element) : null;

        return CreateSuccessResult(request, document.Title, content, ExtractMetadata(document), links);
    }

    private static async Task<WebFetchResult> ProcessWithSmartReaderAsync(
        WebFetchRequest request, string html, IDocument document, CancellationToken ct)
    {
        var article = await new Reader(request.Url, html).GetArticleAsync(ct);

        if (string.IsNullOrEmpty(article.Content))
        {
            var content = HtmlConverter.Convert(document.Body ?? document.DocumentElement, request.Format);
            var links = request.IncludeLinks && document.Body != null ? ExtractLinks(document.Body) : null;
            return CreateSuccessResult(request, document.Title, content, ExtractMetadata(document), links);
        }

        var metadata = UpdateMetadataFromArticle(ExtractMetadata(document), article);
        var articleContent = FormatArticleContent(article, request.Format);
        var articleLinks = request.IncludeLinks && document.Body != null ? ExtractLinks(document.Body) : null;

        return CreateSuccessResult(request, article.Title, articleContent, metadata, articleLinks);
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

            links.Add(new ExtractedLink(Text: text, Url: href));
        }

        return links.Take(50).ToList();
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
            WebFetchOutputFormat.Text => HtmlConverter.Convert(article.Content, WebFetchOutputFormat.Text),
            _ => HtmlConverter.Convert(article.Content, WebFetchOutputFormat.Markdown)
        };
    }

    private static WebFetchResult CreateSuccessResult(
        WebFetchRequest request, string? title, string content, WebPageMetadata metadata, List<ExtractedLink>? links)
    {
        var truncated = content.Length > request.MaxLength;
        if (truncated)
        {
            content = HtmlConverter.Truncate(content, request.MaxLength);
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
}