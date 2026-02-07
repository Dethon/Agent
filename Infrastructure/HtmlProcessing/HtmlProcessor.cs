using AngleSharp;
using AngleSharp.Dom;
using Domain.Contracts;
using Domain.DTOs;
using SmartReader;

namespace Infrastructure.HtmlProcessing;

public record HtmlProcessingResult(
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    IReadOnlyList<ExtractedLink>? Links,
    bool IsPartial,
    string? ErrorMessage);

public static class HtmlProcessor
{
    public static async Task<HtmlProcessingResult> ProcessAsync(BrowseRequest request, string html,
        CancellationToken ct)
    {
        var document = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html), ct);

        if (!string.IsNullOrEmpty(request.Selector))
        {
            return ProcessWithSelector(request, document);
        }

        if (request.UseReadability)
        {
            return await ProcessWithReadabilityAsync(request, html, document, ct);
        }

        return ProcessFullBody(request, document);
    }

    private static HtmlProcessingResult ProcessWithSelector(BrowseRequest request, IDocument document)
    {
        var elements = document.QuerySelectorAll(request.Selector!);
        if (elements.Length == 0)
        {
            return CreatePartialResult(document.Title, ExtractMetadata(document),
                $"CSS selector '{request.Selector}' did not match any elements");
        }

        // Combine content from all matching elements
        var contentParts = elements.Select(e => HtmlConverter.Convert(e, request.Format)).ToList();
        var separator = request.Format == WebFetchOutputFormat.Html ? "\n" : "\n\n---\n\n";
        var content = string.Join(separator, contentParts);

        // Combine links from all matching elements
        var links = request.IncludeLinks
            ? elements.SelectMany(ExtractLinks).DistinctBy(l => l.Url).ToList()
            : null;

        return CreateSuccessResult(request.MaxLength, request.Offset, document.Title, content, request.Format,
            ExtractMetadata(document), links);
    }

    private static HtmlProcessingResult ProcessFullBody(BrowseRequest request, IDocument document)
    {
        var content = HtmlConverter.Convert(document.Body ?? document.DocumentElement, request.Format);
        var links = request.IncludeLinks && document.Body != null ? ExtractLinks(document.Body) : null;

        return CreateSuccessResult(request.MaxLength, request.Offset, document.Title, content, request.Format,
            ExtractMetadata(document), links);
    }

    private static async Task<HtmlProcessingResult> ProcessWithReadabilityAsync(
        BrowseRequest request, string html, IDocument document, CancellationToken ct)
    {
        var article = await new Reader(request.Url, html).GetArticleAsync(ct);

        if (string.IsNullOrEmpty(article.Content))
        {
            return ProcessFullBody(request, document);
        }

        var metadata = UpdateMetadataFromArticle(ExtractMetadata(document), article);
        var articleContent = FormatArticleContent(article, request.Format);
        var articleLinks = request.IncludeLinks && document.Body != null ? ExtractLinks(document.Body) : null;

        return CreateSuccessResult(request.MaxLength, request.Offset, article.Title, articleContent, request.Format,
            metadata,
            articleLinks);
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
            _ => HtmlConverter.Convert(article.Content, WebFetchOutputFormat.Markdown)
        };
    }

    private static HtmlProcessingResult CreateSuccessResult(
        int maxLength, int offset, string? title, string content, WebFetchOutputFormat format, WebPageMetadata metadata,
        List<ExtractedLink>? links)
    {
        var totalLength = content.Length;

        // Apply offset first
        if (offset > 0 && offset < content.Length)
        {
            content = content[offset..];
        }
        else if (offset >= content.Length)
        {
            content = "";
        }

        // Then apply max length truncation
        var truncated = content.Length > maxLength;
        if (truncated)
        {
            content = format == WebFetchOutputFormat.Html
                ? HtmlConverter.TruncateHtml(content, maxLength)
                : HtmlConverter.Truncate(content, maxLength);
        }

        var hasMore = offset + content.Length < totalLength;

        return new HtmlProcessingResult(
            Title: title,
            Content: content,
            ContentLength: totalLength,
            Truncated: truncated || hasMore,
            Metadata: metadata,
            Links: links,
            IsPartial: false,
            ErrorMessage: null
        );
    }

    private static HtmlProcessingResult CreatePartialResult(
        string? title, WebPageMetadata metadata, string errorMessage)
    {
        return new HtmlProcessingResult(
            Title: title,
            Content: errorMessage,
            ContentLength: 0,
            Truncated: false,
            Metadata: metadata,
            Links: null,
            IsPartial: true,
            ErrorMessage: errorMessage
        );
    }
}