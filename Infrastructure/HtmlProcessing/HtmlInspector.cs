using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Domain.Contracts;

namespace Infrastructure.HtmlProcessing;

public static partial class HtmlInspector
{
    private const int PreviewLength = 100;
    private const int MinRepeatingCount = 3;

    public static InspectStructure InspectStructure(IDocument document, string? selectorScope)
    {
        var root = GetScopedElement(document, selectorScope);
        if (root == null)
        {
            return new InspectStructure(null, [], null, [], [], 0);
        }

        var totalTextLength = root.TextContent.Length;

        // Detect main content area
        var mainContent = DetectMainContent(root);

        // Detect repeating elements (product cards, search results, list items)
        var repeatingElements = DetectRepeatingElements(root);

        // Detect navigation elements
        var navigation = DetectNavigation(root);

        // Build hierarchical outline
        var outline = BuildOutline(root);

        // Generate actionable suggestions
        var suggestions = GenerateSuggestions(mainContent, repeatingElements, navigation, root);

        return new InspectStructure(mainContent, repeatingElements, navigation, outline, suggestions, totalTextLength);
    }

    private static ContentRegion? DetectMainContent(IElement root)
    {
        // Priority order for main content detection
        var candidates = new List<(IElement Element, int Score)>();

        // Check semantic elements first
        var main = root.QuerySelector("main");
        if (main != null)
        {
            candidates.Add((main, 100 + (main.TextContent.Length / 100)));
        }

        var article = root.QuerySelector("article");
        if (article != null)
        {
            candidates.Add((article, 90 + (article.TextContent.Length / 100)));
        }

        // Check common content class patterns
        var contentPatterns = new[]
        {
            "[role='main']", ".content", ".main-content", ".post-content",
            ".article-content", ".entry-content", ".page-content", "#content", "#main"
        };

        foreach (var pattern in contentPatterns)
        {
            var element = root.QuerySelector(pattern);
            if (element != null && element.TextContent.Length > 200)
            {
                candidates.Add((element, 80 + (element.TextContent.Length / 100)));
            }
        }

        // Find largest content div that's not navigation/header/footer
        var contentDivs = root.QuerySelectorAll("div")
            .Where(d => !IsNavigationElement(d) && d.TextContent.Length > 500)
            .OrderByDescending(d => d.TextContent.Length)
            .Take(3);

        foreach (var div in contentDivs)
        {
            candidates.Add((div, 50 + (div.TextContent.Length / 100)));
        }

        var best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
        if (best.Element == null)
        {
            return null;
        }

        return new ContentRegion(
            Selector: GenerateSelector(best.Element),
            Preview: GetPreview(best.Element),
            TextLength: best.Element.TextContent.Length);
    }

    private static List<RepeatingElements> DetectRepeatingElements(IElement root)
    {
        var results = new List<RepeatingElements>();
        var classGroups = new Dictionary<string, List<IElement>>();

        // Group elements by their class combinations
        foreach (var element in root.QuerySelectorAll("div, li, article, section, tr"))
        {
            var classes = element.GetAttribute("class");
            if (string.IsNullOrWhiteSpace(classes))
            {
                continue;
            }

            // Use first 1-2 classes as key
            var classKey = string.Join(".", classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
            if (string.IsNullOrEmpty(classKey))
            {
                continue;
            }

            var key = $"{element.TagName.ToLowerInvariant()}.{classKey}";
            if (!classGroups.TryGetValue(key, out var list))
            {
                list = [];
                classGroups[key] = list;
            }

            list.Add(element);
        }

        // Find groups with enough similar elements
        foreach (var (selector, elements) in classGroups.Where(g => g.Value.Count >= MinRepeatingCount))
        {
            // Verify elements are siblings or at same level (not nested within each other)
            if (!AreElementsAtSameLevel(elements))
            {
                continue;
            }

            var sample = elements.First();
            var detectedFields = DetectFieldsInElement(sample);

            results.Add(new RepeatingElements(
                Selector: selector,
                Count: elements.Count,
                Preview: GetPreview(sample),
                DetectedFields: detectedFields.Count > 0 ? detectedFields : null));
        }

        return results
            .OrderByDescending(r => r.Count)
            .Take(5)
            .ToList();
    }

    private static List<string> DetectFieldsInElement(IElement element)
    {
        var fields = new List<string>();

        // Check for common field patterns
        if (element.QuerySelector("h1, h2, h3, h4, h5, h6, .title, .name") != null)
        {
            fields.Add("title");
        }

        if (element.QuerySelector("img") != null)
        {
            fields.Add("image");
        }

        if (element.QuerySelector(".price, [class*='price']") != null ||
            element.TextContent.Contains('$') || element.TextContent.Contains('€'))
        {
            fields.Add("price");
        }

        if (element.QuerySelector("a[href]") != null)
        {
            fields.Add("link");
        }

        if (element.QuerySelector(".description, .summary, p") != null)
        {
            fields.Add("description");
        }

        if (element.QuerySelector(".rating, [class*='star'], [class*='rating']") != null)
        {
            fields.Add("rating");
        }

        if (element.QuerySelector("time, .date, [class*='date']") != null)
        {
            fields.Add("date");
        }

        return fields;
    }

    private static bool AreElementsAtSameLevel(List<IElement> elements)
    {
        if (elements.Count < 2)
        {
            return true;
        }

        // Check if any element is ancestor of another
        var first = elements[0];
        return elements.Skip(1).All(e => !first.Contains(e) && !e.Contains(first));
    }

    private static NavigationInfo? DetectNavigation(IElement root)
    {
        string? paginationSelector = null;
        string? nextPageSelector = null;
        string? prevPageSelector = null;
        string? menuSelector = null;

        // Detect pagination
        var paginationPatterns = new[] { ".pagination", ".pager", "[class*='pagina']", "nav[aria-label*='page']" };
        foreach (var pattern in paginationPatterns)
        {
            var element = root.QuerySelector(pattern);
            if (element != null)
            {
                paginationSelector = pattern;
                break;
            }
        }

        // Detect next/prev links
        var nextPatterns = new[]
            { "a.next", "a[rel='next']", "[class*='next']", "a:has-text('Next')", "a:has-text('>')" };
        foreach (var pattern in nextPatterns)
        {
            try
            {
                var element = root.QuerySelector(pattern);
                if (element != null)
                {
                    nextPageSelector = GenerateSelector(element);
                    break;
                }
            }
            catch
            {
                // Some patterns may not be supported
            }
        }

        var prevPatterns = new[]
            { "a.prev", "a[rel='prev']", "[class*='prev']", "a:has-text('Prev')", "a:has-text('<')" };
        foreach (var pattern in prevPatterns)
        {
            try
            {
                var element = root.QuerySelector(pattern);
                if (element != null)
                {
                    prevPageSelector = GenerateSelector(element);
                    break;
                }
            }
            catch
            {
                // Some patterns may not be supported
            }
        }

        // Detect main navigation
        var navElement = root.QuerySelector("nav") ?? root.QuerySelector("[role='navigation']");
        if (navElement != null)
        {
            menuSelector = GenerateSelector(navElement) + " a";
        }

        if (paginationSelector == null && nextPageSelector == null && prevPageSelector == null && menuSelector == null)
        {
            return null;
        }

        return new NavigationInfo(paginationSelector, nextPageSelector, prevPageSelector, menuSelector);
    }

    private static List<OutlineNode> BuildOutline(IElement root, int maxDepth = 3, int currentDepth = 0)
    {
        if (currentDepth >= maxDepth)
        {
            return [];
        }

        var outline = new List<OutlineNode>();
        var structuralTags = new HashSet<string> { "HEADER", "NAV", "MAIN", "ARTICLE", "SECTION", "ASIDE", "FOOTER" };

        // Get direct structural children
        var structuralChildren = root.Children
            .Where(c => structuralTags.Contains(c.TagName) ||
                        (c.TagName == "DIV" && HasSignificantClass(c) && c.TextContent.Length > 100))
            .Take(10)
            .ToList();

        foreach (var child in structuralChildren)
        {
            var childOutline = BuildOutline(child, maxDepth, currentDepth + 1);

            outline.Add(new OutlineNode(
                Tag: child.TagName.ToLowerInvariant(),
                Selector: GenerateSelector(child),
                Preview: GetPreview(child, 60),
                TextLength: child.TextContent.Length,
                Children: childOutline.Count > 0 ? childOutline : null));
        }

        return outline;
    }

    private static bool HasSignificantClass(IElement element)
    {
        var classes = element.GetAttribute("class");
        if (string.IsNullOrWhiteSpace(classes))
        {
            return false;
        }

        var significantPatterns = new[]
        {
            "content", "main", "article", "post", "entry", "container",
            "wrapper", "body", "page", "section", "sidebar", "widget"
        };

        return significantPatterns.Any(p => classes.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GenerateSuggestions(
        ContentRegion? mainContent,
        List<RepeatingElements> repeatingElements,
        NavigationInfo? navigation,
        IElement root)
    {
        var suggestions = new List<string>();

        if (mainContent != null)
        {
            suggestions.Add($"Main content: use selector='{mainContent.Selector}'");
        }

        foreach (var repeating in repeatingElements.Take(3))
        {
            var fieldsInfo = repeating.DetectedFields != null
                ? $" (contains: {string.Join(", ", repeating.DetectedFields)})"
                : "";
            suggestions.Add($"Found {repeating.Count} items: use selector='{repeating.Selector}'{fieldsInfo}");
        }

        if (navigation?.NextPageSelector != null)
        {
            suggestions.Add($"Next page: use WebClick(selector='{navigation.NextPageSelector}')");
        }

        if (navigation?.PaginationSelector != null)
        {
            suggestions.Add($"Pagination available at '{navigation.PaginationSelector}'");
        }

        var formCount = root.QuerySelectorAll("form").Length;
        if (formCount > 0)
        {
            suggestions.Add($"Page has {formCount} form(s): use WebInspect(mode='forms') for details");
        }

        if (suggestions.Count == 0)
        {
            suggestions.Add(
                "No clear structure detected. Try WebInspect(mode='interactive') to find clickable elements");
        }

        return suggestions;
    }

    private static bool IsNavigationElement(IElement element)
    {
        var tag = element.TagName.ToUpperInvariant();
        if (tag is "NAV" or "HEADER" or "FOOTER")
        {
            return true;
        }

        var classes = element.GetAttribute("class") ?? "";
        var id = element.GetAttribute("id") ?? "";
        var role = element.GetAttribute("role") ?? "";

        var navPatterns = new[] { "nav", "menu", "header", "footer", "sidebar", "widget" };
        return navPatterns.Any(p =>
            classes.Contains(p, StringComparison.OrdinalIgnoreCase) ||
            id.Contains(p, StringComparison.OrdinalIgnoreCase) ||
            role.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetPreview(IElement element, int length = PreviewLength)
    {
        var text = CollapseWhitespace(element.TextContent.Trim());
        if (text.Length <= length)
        {
            return text;
        }

        return text[..(length - 3)] + "...";
    }

    public static InspectSearchResult SearchText(IDocument document, string query, bool regex,
        int maxResults, string? selectorScope)
    {
        // Return empty results for empty/whitespace query (unless regex, which might match on empty)
        if (!regex && string.IsNullOrWhiteSpace(query))
        {
            return new InspectSearchResult(query, 0, []);
        }

        var root = GetScopedElement(document, selectorScope);
        if (root == null)
        {
            return new InspectSearchResult(query, 0, []);
        }

        var matches = new List<InspectSearchMatch>();
        var pattern = regex ? new Regex(query, RegexOptions.IgnoreCase) : null;

        foreach (var element in GetTextElements(root))
        {
            var text = element.TextContent;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var found = regex
                ? pattern!.IsMatch(text)
                : text.Contains(query, StringComparison.OrdinalIgnoreCase);

            if (!found)
            {
                continue;
            }

            var matchText = regex
                ? pattern!.Match(text).Value
                : ExtractMatchText(text, query);

            var context = ExtractContext(text, matchText, 50);
            var selector = GenerateSelector(element);
            var nearestHeading = FindNearestHeading(element);

            matches.Add(new InspectSearchMatch(matchText, context, selector, nearestHeading));

            if (matches.Count >= maxResults)
            {
                break;
            }
        }

        return new InspectSearchResult(query, matches.Count, matches);
    }

    public static IReadOnlyList<InspectForm> InspectForms(IDocument document, string? selectorScope)
    {
        var root = GetScopedElement(document, selectorScope);
        if (root == null)
        {
            return [];
        }

        return root.QuerySelectorAll("form")
            .Select(form => new
            {
                form,
                fields = ExtractFormFields(form)
            })
            .Select(t => new
            {
                t,
                buttons = ExtractFormButtons(t.form)
            })
            .Select(t => new InspectForm(Name: t.t.form.GetAttribute("name"),
                Action: t.t.form.GetAttribute("action"),
                Method: t.t.form.GetAttribute("method")?.ToUpperInvariant(), Selector: GenerateSelector(t.t.form),
                Fields: t.t.fields, Buttons: t.buttons)).ToList();
    }

    public static InspectInteractive InspectInteractive(IDocument document, string? selectorScope)
    {
        var root = GetScopedElement(document, selectorScope);
        if (root == null)
        {
            return new InspectInteractive([], []);
        }

        var buttons = ExtractButtons(root);
        var links = ExtractLinks(root);

        return new InspectInteractive(buttons, links);
    }

    private static IElement? GetScopedElement(IDocument document, string? selectorScope)
    {
        if (string.IsNullOrEmpty(selectorScope))
        {
            return document.Body ?? document.DocumentElement;
        }

        return document.QuerySelector(selectorScope);
    }

    private static List<InspectFormField> ExtractFormFields(IElement form)
    {
        return form.QuerySelectorAll("input, select, textarea")
            .Select(input => new
            {
                input,
                type = input.TagName.ToLowerInvariant() switch
                {
                    "select" => "select",
                    "textarea" => "textarea",
                    _ => input.GetAttribute("type") ?? "text"
                }
            })
            .Where(t => t.type is not ("hidden" or "submit" or "button"))
            .Select(t => new
            {
                t,
                name = t.input.GetAttribute("name")
            })
            .Select(t => new
            {
                t,
                id = t.t.input.GetAttribute("id")
            })
            .Select(t => new
            {
                t,
                label = FindLabel(t.t.t.input, form)
            })
            .Select(t => new InspectFormField(Type: t.t.t.t.type, Name: t.t.t.name, Label: t.label,
                Placeholder: t.t.t.t.input.GetAttribute("placeholder"),
                Selector: GenerateSelector(t.t.t.t.input),
                Required: t.t.t.t.input.HasAttribute("required"))).ToList();
    }

    private static string? FindLabel(IElement input, IElement form)
    {
        var id = input.GetAttribute("id");
        if (!string.IsNullOrEmpty(id))
        {
            var label = form.QuerySelector($"label[for='{id}']");
            if (label != null)
            {
                return CollapseWhitespace(label.TextContent.Trim());
            }
        }

        var parentLabel = input.Closest("label");
        if (parentLabel == null)
        {
            return null;
        }

        // Get the label text, excluding the input's text content (if any)
        var inputText = input.TextContent;
        var labelText = parentLabel.TextContent;

        var text = string.IsNullOrEmpty(inputText)
            ? labelText.Trim()
            : labelText.Replace(inputText, "").Trim();

        return CollapseWhitespace(text);
    }

    private static List<InspectButton> ExtractFormButtons(IElement form)
    {
        return form
            .QuerySelectorAll("button, input[type='submit'], input[type='button']")
            .Select(button => new
            {
                button,
                text = button.TagName.Equals("INPUT", StringComparison.OrdinalIgnoreCase)
                    ? button.GetAttribute("value")
                    : button.TextContent.Trim()
            })
            .Select(t => new InspectButton(
                Tag: t.button.TagName.ToLowerInvariant(),
                Text: CollapseWhitespace(t.text ?? ""),
                Selector: GenerateSelector(t.button)))
            .ToList();
    }

    private static List<InspectButton> ExtractButtons(IElement root)
    {
        var buttonGroups = new Dictionary<string, List<IElement>>();

        foreach (var button in root.QuerySelectorAll("button, input[type='submit'], input[type='button']"))
        {
            var text = button.TagName.Equals("INPUT", StringComparison.OrdinalIgnoreCase)
                ? button.GetAttribute("value")
                : button.TextContent.Trim();

            text = CollapseWhitespace(text ?? "");
            if (string.IsNullOrEmpty(text))
            {
                text = button.GetAttribute("aria-label") ?? button.GetAttribute("title") ?? "(no text)";
            }

            if (!buttonGroups.TryGetValue(text, out var list))
            {
                list = [];
                buttonGroups[text] = list;
            }

            list.Add(button);
        }

        return buttonGroups
            .OrderByDescending(kvp => kvp.Value.Count)
            .Take(20)
            .Select(kvp => new InspectButton(
                Tag: "button",
                Text: kvp.Key,
                Selector: GenerateGroupSelector(kvp.Value),
                Count: kvp.Value.Count))
            .ToList();
    }

    private static List<InspectLink> ExtractLinks(IElement root)
    {
        var linkGroups = new Dictionary<string, List<IElement>>();

        foreach (var link in root.QuerySelectorAll("a[href]"))
        {
            var text = CollapseWhitespace(link.TextContent.Trim());
            if (string.IsNullOrEmpty(text))
            {
                text = link.GetAttribute("aria-label") ?? link.GetAttribute("title") ?? "(no text)";
            }

            if (text.Length > 50)
            {
                text = text[..47] + "...";
            }

            if (!linkGroups.TryGetValue(text, out var list))
            {
                list = [];
                linkGroups[text] = list;
            }

            list.Add(link);
        }

        return linkGroups
            .OrderByDescending(kvp => kvp.Value.Count)
            .Take(30)
            .Select(kvp => new InspectLink(
                Text: kvp.Key,
                Selector: GenerateGroupSelector(kvp.Value),
                Count: kvp.Value.Count))
            .ToList();
    }

    private static IEnumerable<IElement> GetTextElements(IElement root)
    {
        return root.QuerySelectorAll("p, li, td, th, span, div, h1, h2, h3, h4, h5, h6, a, label");
    }

    private static string ExtractMatchText(string text, string query)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? query
            : text.Substring(index, query.Length);
    }

    private static string ExtractContext(string text, string match, int contextChars)
    {
        var index = text.IndexOf(match, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text.Length > contextChars * 2 ? text[..(contextChars * 2)] + "..." : text;
        }

        var start = Math.Max(0, index - contextChars);
        var end = Math.Min(text.Length, index + match.Length + contextChars);

        var context = text[start..end];
        if (start > 0)
        {
            context = "..." + context;
        }

        if (end < text.Length)
        {
            context += "...";
        }

        return CollapseWhitespace(context);
    }

    private static string? FindNearestHeading(IElement element)
    {
        var current = element.PreviousElementSibling;
        while (current != null)
        {
            if (current.TagName.StartsWith('H') && current.TagName.Length == 2 &&
                char.IsDigit(current.TagName[1]))
            {
                return CollapseWhitespace(current.TextContent.Trim());
            }

            current = current.PreviousElementSibling;
        }

        var parent = element.ParentElement;
        while (parent != null)
        {
            var heading = parent.QuerySelector("h1, h2, h3, h4, h5, h6");
            if (heading != null)
            {
                return CollapseWhitespace(heading.TextContent.Trim());
            }

            parent = parent.ParentElement;
        }

        return null;
    }

    private static string GenerateSelector(IElement element)
    {
        var id = element.GetAttribute("id");
        if (!string.IsNullOrEmpty(id))
        {
            return $"{element.TagName.ToLowerInvariant()}#{EscapeCssIdentifier(id)}";
        }

        var name = element.GetAttribute("name");
        if (!string.IsNullOrEmpty(name))
        {
            return $"{element.TagName.ToLowerInvariant()}[name='{EscapeCssAttributeValue(name)}']";
        }

        var tag = element.TagName.ToLowerInvariant();
        var classes = element.GetAttribute("class")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes is { Length: > 0 })
        {
            var primaryClass = classes[0];
            var selector = $"{tag}.{EscapeCssIdentifier(primaryClass)}";

            var parent = element.ParentElement;
            if (parent == null)
            {
                return selector;
            }

            // Check if selector is unique within parent
            var matchingInParent = parent.QuerySelectorAll(selector);
            if (matchingInParent.Length <= 1)
            {
                return selector;
            }

            // Use nth-of-type with the index among same-tag siblings with same class
            var nthOfTypeIndex = GetNthOfTypeIndex(element, parent, selector);
            return $"{selector}:nth-of-type({nthOfTypeIndex})";
        }

        var parentElement = element.ParentElement;
        if (parentElement == null)
        {
            return tag;
        }

        // Check if tag alone is unique
        var sameTagSiblings = parentElement.Children.Where(c => c.TagName == element.TagName).ToList();
        if (sameTagSiblings.Count <= 1)
        {
            return tag;
        }

        var index = sameTagSiblings.IndexOf(element) + 1;
        return $"{tag}:nth-of-type({index})";
    }

    private static int GetNthOfTypeIndex(IElement element, IElement parent, string selector)
    {
        // Get all elements matching the selector, then find position among same-tag siblings
        var matchingElements = parent.QuerySelectorAll(selector).ToList();
        var sameTagElements = matchingElements.Where(e => e.TagName == element.TagName).ToList();
        return sameTagElements.IndexOf(element) + 1;
    }

    private static string EscapeCssIdentifier(string identifier)
    {
        // Escape special CSS characters in identifiers (IDs, class names)
        // Characters that need escaping: !"#$%&'()*+,./:;<=>?@[\]^`{|}~
        if (string.IsNullOrEmpty(identifier))
        {
            return identifier;
        }

        var sb = new StringBuilder();
        foreach (var c in identifier)
        {
            if (c is ':' or '.' or '[' or ']' or '#' or '>' or '+' or '~' or ',' or ' ' or '(' or ')' or '"' or '\''
                or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string EscapeCssAttributeValue(string value)
    {
        // Escape single quotes in attribute values
        return value.Replace("'", "\\'");
    }

    private static string GenerateGroupSelector(IReadOnlyList<IElement> elements)
    {
        if (elements.Count == 1)
        {
            return GenerateSelector(elements[0]);
        }

        // Find common classes across all elements
        var firstElement = elements[0];
        var tag = firstElement.TagName.ToLowerInvariant();

        var commonClasses = firstElement.GetAttribute("class")?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet() ?? [];

        foreach (var element in elements.Skip(1))
        {
            var classes = element.GetAttribute("class")?
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet() ?? [];
            commonClasses.IntersectWith(classes);
        }

        return commonClasses.Count > 0
            ? $"{tag}.{commonClasses.First()}"
            : tag; // No common class - return just the tag
    }

    private static string CollapseWhitespace(string text)
    {
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}