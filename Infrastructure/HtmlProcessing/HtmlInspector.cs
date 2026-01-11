using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Domain.Contracts;

namespace Infrastructure.HtmlProcessing;

public static partial class HtmlInspector
{
    public static InspectStructure InspectStructure(IDocument document, string? selectorScope,
        int maxHeadings = 15, int maxSections = 8, int previewLength = 200)
    {
        var root = GetScopedElement(document, selectorScope);
        if (root == null)
        {
            return new InspectStructure([], [], 0, 0, 0, null, 0);
        }

        var headings = ExtractHeadings(root, maxHeadings);
        var sections = ExtractSections(root, maxSections);
        var formCount = root.QuerySelectorAll("form").Length;
        var buttonCount = root.QuerySelectorAll("button, input[type='submit'], input[type='button']").Length;
        var linkCount = root.QuerySelectorAll("a[href]").Length;
        var totalTextLength = root.TextContent.Length;

        var preview = root.TextContent.Trim();
        if (preview.Length > previewLength)
        {
            preview = preview[..previewLength] + "...";
        }

        preview = CollapseWhitespace(preview);

        return new InspectStructure(headings, sections, formCount, buttonCount, linkCount, preview, totalTextLength);
    }

    public static InspectSearchResult SearchText(IDocument document, string query, bool regex,
        int maxResults, string? selectorScope)
    {
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

    private static List<InspectHeading> ExtractHeadings(IElement root, int maxHeadings)
    {
        // Prioritize by heading level (h1 first, then h2, etc.) and take top N
        return root.QuerySelectorAll("h1, h2, h3, h4, h5, h6")
            .Select(h =>
            {
                var level = int.Parse(h.TagName[1..]);
                var text = CollapseWhitespace(h.TextContent.Trim());
                if (text.Length > 80)
                {
                    text = text[..77] + "...";
                }

                return new InspectHeading(
                    Level: level,
                    Text: text,
                    Id: h.GetAttribute("id"),
                    Selector: GenerateSelector(h));
            })
            .Where(h => !string.IsNullOrWhiteSpace(h.Text))
            .OrderBy(h => h.Level)
            .Take(maxHeadings)
            .ToList();
    }

    private static List<InspectSection> ExtractSections(IElement root, int maxSections)
    {
        // Prioritize: main > article > section, then by content size
        var sectionTags = new[] { "main", "article", "section", "aside", "nav", "header", "footer" };
        var tagPriority = sectionTags.Select((tag, i) => (tag, priority: i)).ToDictionary(x => x.tag, x => x.priority);

        return sectionTags
            .SelectMany(root.QuerySelectorAll, (tag, section) => new
            {
                Section = new InspectSection(
                    Tag: tag,
                    Id: section.GetAttribute("id"),
                    ClassName: section.GetAttribute("class")?.Split(' ').FirstOrDefault(),
                    Selector: GenerateSelector(section),
                    TextLength: section.TextContent.Length),
                Priority = tagPriority[tag]
            })
            .Where(x => x.Section.TextLength > 100) // Skip tiny sections
            .OrderBy(x => x.Priority)
            .ThenByDescending(x => x.Section.TextLength)
            .Take(maxSections)
            .Select(x => x.Section)
            .ToList();
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

        var text = parentLabel.TextContent.Replace(input.TextContent, "").Trim();
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
            return $"{element.TagName.ToLowerInvariant()}#{id}";
        }

        var name = element.GetAttribute("name");
        if (!string.IsNullOrEmpty(name))
        {
            return $"{element.TagName.ToLowerInvariant()}[name='{name}']";
        }

        var classes = element.GetAttribute("class")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes is { Length: > 0 })
        {
            var primaryClass = classes[0];
            var selector = $"{element.TagName.ToLowerInvariant()}.{primaryClass}";

            var parent = element.ParentElement;
            if (parent == null)
            {
                return selector;
            }

            var siblings = parent.QuerySelectorAll(selector);
            if (siblings.Length <= 1)
            {
                return selector;
            }

            var index = siblings.Index().FirstOrDefault(x => x.Item == element).Index + 1;
            return $"{selector}:nth-child({index})";
        }

        var tag = element.TagName.ToLowerInvariant();
        var parentElement = element.ParentElement;
        if (parentElement == null)
        {
            return tag;
        }

        var siblings2 = parentElement.QuerySelectorAll(tag);
        if (siblings2.Length <= 1)
        {
            return tag;
        }

        var index2 = siblings2.Index().FirstOrDefault(x => x.Item == element).Index + 1;
        return $"{tag}:nth-child({index2})";
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