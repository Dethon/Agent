using Domain.DTOs;
using Infrastructure.HtmlProcessing;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public enum ResponseTier
{
    Widget,
    FullPage,
    Focused
}

public static class PostActionAnalyzer
{
    private const double MajorChangeThreshold = 0.5;
    private const int MaxContentLength = 10000;
    private const int FocusedAreaMaxLength = 5000;
    private const int ContentSampleSize = 200;

    public static ResponseTier DetermineResponseTier(bool widgetDetected, bool urlChanged, double contentChangeFraction)
    {
        if (widgetDetected) return ResponseTier.Widget;
        if (urlChanged || contentChangeFraction > MajorChangeThreshold) return ResponseTier.FullPage;
        return ResponseTier.Focused;
    }

    public static double ComputeContentChangeFraction(string before, string after)
    {
        if (string.IsNullOrEmpty(before)) return string.IsNullOrEmpty(after) ? 0.0 : 1.0;
        if (string.IsNullOrEmpty(after)) return 1.0;
        if (before == after) return 0.0;

        var maxLen = Math.Max(before.Length, after.Length);
        var sampleSize = Math.Min(ContentSampleSize, Math.Min(before.Length, after.Length));

        var startDiffs = Enumerable.Range(0, sampleSize).Count(i => before[i] != after[i]);
        var endDiffs = Enumerable.Range(0, sampleSize).Count(i => before[before.Length - 1 - i] != after[after.Length - 1 - i]);

        var totalSampled = sampleSize * 2;
        var sampleFraction = totalSampled > 0 ? (double)(startDiffs + endDiffs) / totalSampled : 0.0;

        var lengthDiff = Math.Abs(before.Length - after.Length);
        var lengthFraction = (double)lengthDiff / maxLen;

        // If all sampled characters differ, it's completely different regardless of length
        if (sampleFraction >= 1.0) return 1.0;

        return Math.Min(1.0, sampleFraction * 0.7 + lengthFraction * 0.3);
    }

    public static async Task<string> AnalyzeAsync(
        IPage page,
        string targetSelector,
        string urlBefore,
        string contentBefore,
        CancellationToken ct = default)
    {
        var urlChanged = page.Url != urlBefore;

        var widget = await WidgetDetector.DetectWidgetAsync(page, targetSelector);

        var htmlAfter = await page.ContentAsync();
        var contentAfter = HtmlConverter.Convert(htmlAfter, WebFetchOutputFormat.Markdown);

        var changeFraction = ComputeContentChangeFraction(contentBefore, contentAfter);
        var tier = DetermineResponseTier(widget != null, urlChanged, changeFraction);

        return tier switch
        {
            ResponseTier.Widget => WidgetDetector.FormatWidgetContent(widget!),
            ResponseTier.FullPage => HtmlConverter.Truncate(contentAfter, MaxContentLength),
            ResponseTier.Focused => await ExtractFocusedAreaAsync(page, targetSelector, contentAfter),
            _ => HtmlConverter.Truncate(contentAfter, MaxContentLength)
        };
    }

    public static string GetContentSnapshot(string html) =>
        HtmlConverter.Convert(html, WebFetchOutputFormat.Markdown);

    private static async Task<string> ExtractFocusedAreaAsync(IPage page, string targetSelector, string fullContent)
    {
        var focusedHtml = await page.EvaluateAsync<string?>("""
            (selector) => {
                const target = document.querySelector(selector);
                if (!target) return null;

                const containerTags = ['FORM', 'SECTION', 'ARTICLE', 'MAIN', 'DIALOG'];
                let container = target.parentElement;
                let depth = 0;
                while (container && depth < 4) {
                    if (containerTags.includes(container.tagName) ||
                        container.getAttribute('role') === 'dialog' ||
                        container.getAttribute('role') === 'form' ||
                        container.getAttribute('role') === 'region') {
                        break;
                    }
                    container = container.parentElement;
                    depth++;
                }

                if (!container || depth >= 4) {
                    container = target.parentElement?.parentElement || target.parentElement || target;
                }

                return container ? container.outerHTML : null;
            }
            """, targetSelector);

        if (!string.IsNullOrEmpty(focusedHtml))
        {
            var focusedContent = HtmlConverter.Convert(focusedHtml, WebFetchOutputFormat.Markdown);
            if (focusedContent.Length > 50)
                return HtmlConverter.Truncate(focusedContent, FocusedAreaMaxLength);
        }

        return HtmlConverter.Truncate(fullContent, MaxContentLength);
    }
}
