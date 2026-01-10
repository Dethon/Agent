using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Domain.DTOs;

namespace Infrastructure.HtmlProcessing;

public static partial class HtmlConverter
{
    public static string Convert(IElement? element, WebFetchOutputFormat format)
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

    public static string Convert(string html, WebFetchOutputFormat format)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        return format switch
        {
            WebFetchOutputFormat.Html => html,
            WebFetchOutputFormat.Text => HtmlToText(html),
            _ => HtmlToMarkdown(html)
        };
    }

    public static string Truncate(string text, int maxLength)
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

    public static string TruncateHtml(string html, int maxLength)
    {
        if (html.Length <= maxLength)
        {
            return html;
        }

        var targetLength = maxLength - 50; // Reserve space for closing tags and message
        var truncated = html[..targetLength];

        // Find last complete tag to avoid cutting in the middle of a tag
        var lastTagEnd = truncated.LastIndexOf('>');
        var lastTagStart = truncated.LastIndexOf('<');

        // If we're in the middle of a tag, cut before it
        if (lastTagStart > lastTagEnd)
        {
            truncated = truncated[..lastTagStart];
        }
        else if (lastTagEnd > 0)
        {
            truncated = truncated[..(lastTagEnd + 1)];
        }

        // Find unclosed tags and close them
        var openTags = new Stack<string>();
        var tagPattern = TagNameRegex();

        foreach (Match match in tagPattern.Matches(truncated))
        {
            var tagName = match.Groups[2].Value.ToLowerInvariant();
            var isClosing = match.Value.StartsWith("</");
            var isSelfClosing = match.Value.EndsWith("/>") ||
                                IsSelfClosingTag(tagName);

            if (isSelfClosing)
            {
                continue;
            }

            if (isClosing)
            {
                if (openTags.Count > 0 && openTags.Peek() == tagName)
                {
                    openTags.Pop();
                }
            }
            else
            {
                openTags.Push(tagName);
            }
        }

        // Close any unclosed tags
        while (openTags.Count > 0)
        {
            truncated += $"</{openTags.Pop()}>";
        }

        return truncated + "\n<!-- Content truncated -->";
    }

    private static bool IsSelfClosingTag(string tagName)
    {
        return tagName is "br" or "hr" or "img" or "input" or "meta" or "link" or "area" or "base" or "col" or "embed"
            or "param" or "source" or "track" or "wbr";
    }

    [GeneratedRegex(@"<(/?)(\w+)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TagNameRegex();

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