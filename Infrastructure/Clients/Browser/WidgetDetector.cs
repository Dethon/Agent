using System.Text;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public enum WidgetType
{
    Datepicker,
    Autocomplete,
    Dropdown,
    Slider
}

public record DetectedWidget(
    WidgetType Type,
    string? Label,
    string? CurrentValue,
    IReadOnlyList<WidgetOption> Options,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<NearbyAction> NearbyActions);

public record WidgetOption(string Text, string Selector);

public record NearbyAction(string Text, string Selector, string ElementType);

public static class WidgetDetector
{
    private const int MaxOptionsToShow = 15;

    public static async Task<DetectedWidget?> DetectWidgetAsync(IPage page, string targetSelector)
    {
        var widgetInfo = await page.EvaluateAsync<WidgetJsResult?>("""
            (targetSelector) => {
                const target = document.querySelector(targetSelector);
                if (!target) return null;

                const targetRect = target.getBoundingClientRect();
                const proximity = 500;

                function isNear(el) {
                    const rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0 &&
                        Math.abs(rect.top - targetRect.top) < proximity &&
                        Math.abs(rect.left - targetRect.left) < proximity * 2;
                }

                function isVisible(el) {
                    const style = window.getComputedStyle(el);
                    return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0'
                        && el.getBoundingClientRect().height > 0;
                }

                function getSelector(el) {
                    if (el.id) return '#' + CSS.escape(el.id);
                    if (el.getAttribute('name')) return el.tagName.toLowerCase() + '[name="' + el.getAttribute('name') + '"]';
                    const classes = el.className && typeof el.className === 'string'
                        ? '.' + el.className.trim().split(/\s+/).slice(0, 2).map(c => CSS.escape(c)).join('.')
                        : '';
                    const tag = el.tagName.toLowerCase();
                    const parent = el.parentElement;
                    if (!parent) return tag + classes;
                    const siblings = Array.from(parent.children).filter(c => c.tagName === el.tagName);
                    if (siblings.length === 1) return tag + classes;
                    const idx = siblings.indexOf(el) + 1;
                    return tag + classes + ':nth-of-type(' + idx + ')';
                }

                function getLabel(el) {
                    if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
                    if (el.id) {
                        const label = document.querySelector('label[for="' + el.id + '"]');
                        if (label) return label.textContent.trim();
                    }
                    const parentLabel = el.closest('label');
                    if (parentLabel) return parentLabel.textContent.replace(el.textContent || '', '').trim();
                    if (el.placeholder) return el.placeholder;
                    return null;
                }

                // 1. Check for datepicker
                const datepickerSelectors = [
                    '[class*="calendar"]', '[class*="datepicker"]', '[class*="date-picker"]',
                    '[role="dialog"][class*="date"]', '[role="grid"]',
                    '.flatpickr-calendar', '.react-datepicker', '.MuiPickersCalendar-root',
                    '.pikaday', '.ui-datepicker'
                ];
                for (const sel of datepickerSelectors) {
                    const els = document.querySelectorAll(sel);
                    for (const el of els) {
                        if (!isVisible(el) || !isNear(el)) continue;
                        const days = Array.from(el.querySelectorAll(
                            '[class*="day"]:not([class*="disabled"]):not([class*="outside"]):not([aria-disabled="true"]), ' +
                            'td[data-date]:not(.disabled), td:not(.disabled) a'
                        )).filter(d => isVisible(d) && d.textContent.trim().length <= 2);
                        const options = days.slice(0, 31).map(d => ({
                            text: d.textContent.trim(),
                            selector: getSelector(d)
                        }));
                        const header = el.querySelector(
                            '[class*="month"], [class*="title"], [class*="header"], .flatpickr-current-month'
                        );
                        const metadata = {};
                        if (header) metadata.visibleMonth = header.textContent.trim();
                        const prevBtn = el.querySelector('[class*="prev"], [aria-label*="prev"], [class*="left"]');
                        const nextBtn = el.querySelector('[class*="next"], [aria-label*="next"], [class*="right"]');
                        if (prevBtn) options.unshift({ text: '← Previous month', selector: getSelector(prevBtn) });
                        if (nextBtn) options.push({ text: 'Next month →', selector: getSelector(nextBtn) });

                        return {
                            type: 'datepicker',
                            label: getLabel(target),
                            currentValue: target.value || null,
                            options: options,
                            metadata: metadata
                        };
                    }
                }

                // 2. Check for autocomplete/suggestion list
                const autocompleteSelectors = [
                    '[role="listbox"]', '[class*="autocomplete"]', '[class*="suggestions"]',
                    '[class*="typeahead"]', '[class*="dropdown-menu"]',
                    '[class*="combobox"] [role="option"]', 'ul[class*="option"]',
                    '[class*="select-menu"]', '[class*="results"]'
                ];
                const expandedTrigger = target.getAttribute('aria-expanded') === 'true' ||
                    target.closest('[aria-expanded="true"]');
                if (expandedTrigger || target.tagName === 'INPUT') {
                    for (const sel of autocompleteSelectors) {
                        const els = document.querySelectorAll(sel);
                        for (const el of els) {
                            if (!isVisible(el) || !isNear(el)) continue;
                            const items = Array.from(el.querySelectorAll(
                                '[role="option"], li, [class*="item"], [class*="option"]'
                            )).filter(i => isVisible(i) && i.textContent.trim().length > 0);
                            if (items.length === 0) continue;
                            const options = items.slice(0, 20).map(i => ({
                                text: i.textContent.trim().substring(0, 100),
                                selector: getSelector(i)
                            }));
                            return {
                                type: 'autocomplete',
                                label: getLabel(target),
                                currentValue: target.value || null,
                                options: options,
                                metadata: null
                            };
                        }
                    }
                }

                // 3. Check for custom dropdown (not native <select>)
                const dropdownSelectors = [
                    '[role="listbox"]', '[role="menu"]',
                    '[class*="dropdown"][class*="open"]', '[class*="dropdown"][class*="show"]',
                    '[class*="select"][class*="open"]', '[class*="select"][class*="show"]',
                    '[class*="menu"][class*="open"]', '[class*="menu"][class*="show"]'
                ];
                for (const sel of dropdownSelectors) {
                    const els = document.querySelectorAll(sel);
                    for (const el of els) {
                        if (!isVisible(el) || !isNear(el)) continue;
                        const items = Array.from(el.querySelectorAll(
                            '[role="option"], [role="menuitem"], li, [class*="item"], [class*="option"]'
                        )).filter(i => isVisible(i) && i.textContent.trim().length > 0);
                        if (items.length === 0) continue;
                        const totalOptions = items.length;
                        const options = items.slice(0, 20).map(i => ({
                            text: i.textContent.trim().substring(0, 100),
                            selector: getSelector(i)
                        }));
                        const metadata = { totalOptions: String(totalOptions) };
                        const selected = items.find(i =>
                            i.getAttribute('aria-selected') === 'true' || i.classList.contains('selected'));
                        return {
                            type: 'dropdown',
                            label: getLabel(target),
                            currentValue: selected ? selected.textContent.trim() : (target.textContent.trim() || null),
                            options: options,
                            metadata: metadata
                        };
                    }
                }

                // 4. Check for slider/range input
                if (target.type === 'range' || target.getAttribute('role') === 'slider') {
                    return {
                        type: 'slider',
                        label: getLabel(target),
                        currentValue: target.value,
                        options: [],
                        metadata: {
                            min: target.min || '0',
                            max: target.max || '100',
                            step: target.step || '1'
                        }
                    };
                }

                return null;
            }
            """, targetSelector);

        if (widgetInfo == null) return null;

        var type = widgetInfo.Type switch
        {
            "datepicker" => WidgetType.Datepicker,
            "autocomplete" => WidgetType.Autocomplete,
            "dropdown" => WidgetType.Dropdown,
            "slider" => WidgetType.Slider,
            _ => WidgetType.Dropdown
        };

        var options = widgetInfo.Options?
            .Select(o => new WidgetOption(o.Text, o.Selector))
            .ToList() ?? [];

        var metadata = widgetInfo.Metadata?
            .Where(kvp => kvp.Value != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

        var nearbyActions = await GetNearbyActionsAsync(page, targetSelector);

        return new DetectedWidget(type, widgetInfo.Label, widgetInfo.CurrentValue, options,
            metadata?.Count > 0 ? metadata : null, nearbyActions);
    }

    private static async Task<IReadOnlyList<NearbyAction>> GetNearbyActionsAsync(IPage page, string targetSelector)
    {
        var actions = await page.EvaluateAsync<List<NearbyActionJs>?>("""
            (targetSelector) => {
                const target = document.querySelector(targetSelector);
                if (!target) return [];

                const targetRect = target.getBoundingClientRect();
                const proximity = 400;

                function isNear(el) {
                    const rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0 &&
                        Math.abs(rect.top - targetRect.top) < proximity &&
                        Math.abs(rect.left - targetRect.left) < proximity * 2;
                }

                function isVisible(el) {
                    const style = window.getComputedStyle(el);
                    return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0'
                        && el.getBoundingClientRect().height > 0;
                }

                function getSelector(el) {
                    if (el.id) return '#' + CSS.escape(el.id);
                    if (el.getAttribute('name')) return el.tagName.toLowerCase() + '[name="' + el.getAttribute('name') + '"]';
                    const classes = el.className && typeof el.className === 'string'
                        ? '.' + el.className.trim().split(/\s+/).slice(0, 2).map(c => CSS.escape(c)).join('.')
                        : '';
                    const tag = el.tagName.toLowerCase();
                    const parent = el.parentElement;
                    if (!parent) return tag + classes;
                    const siblings = Array.from(parent.children).filter(c => c.tagName === el.tagName);
                    if (siblings.length === 1) return tag + classes;
                    const idx = siblings.indexOf(el) + 1;
                    return tag + classes + ':nth-of-type(' + idx + ')';
                }

                function getLabel(el) {
                    if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
                    if (el.id) {
                        const label = document.querySelector('label[for="' + el.id + '"]');
                        if (label) return label.textContent.trim();
                    }
                    if (el.placeholder) return el.placeholder;
                    if (el.textContent.trim().length < 50) return el.textContent.trim();
                    return el.tagName.toLowerCase();
                }

                const nearby = [];
                const actionableSelectors = 'input:not([type="hidden"]), select, textarea, button, a[href]';
                const elements = document.querySelectorAll(actionableSelectors);

                for (const el of elements) {
                    if (el === target || !isVisible(el) || !isNear(el)) continue;
                    const label = getLabel(el);
                    if (!label) continue;
                    nearby.push({
                        text: label.substring(0, 80),
                        selector: getSelector(el),
                        elementType: el.tagName.toLowerCase()
                    });
                    if (nearby.length >= 8) break;
                }

                return nearby;
            }
            """, targetSelector);

        return actions?
            .Select(a => new NearbyAction(a.Text, a.Selector, a.ElementType))
            .ToList() ?? [];
    }

    public static string FormatWidgetContent(DetectedWidget widget)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"[Widget: {widget.Type.ToString().ToLowerInvariant()}]");

        switch (widget.Type)
        {
            case WidgetType.Datepicker:
                FormatDatepicker(sb, widget);
                break;
            case WidgetType.Autocomplete:
                FormatAutocomplete(sb, widget);
                break;
            case WidgetType.Dropdown:
                FormatDropdown(sb, widget);
                break;
            case WidgetType.Slider:
                FormatSlider(sb, widget);
                break;
        }

        if (widget.NearbyActions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[Nearby actions]");
            foreach (var action in widget.NearbyActions)
            {
                sb.AppendLine($"- \"{action.Text}\" ({action.ElementType}) → selector: {action.Selector}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void FormatDatepicker(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "date input";
        sb.AppendLine($"Status: Calendar opened for \"{label}\"");
        sb.AppendLine($"Current value: {widget.CurrentValue ?? "(none)"}");

        if (widget.Metadata?.TryGetValue("visibleMonth", out var month) == true)
        {
            sb.AppendLine($"Visible month: {month}");
        }

        sb.AppendLine();
        if (widget.Options.Count == 0)
        {
            sb.AppendLine("No selectable dates visible.");
        }
        else
        {
            sb.AppendLine("Available dates/navigation:");
            foreach (var opt in widget.Options.Take(MaxOptionsToShow))
            {
                sb.AppendLine($"- \"{opt.Text}\" → selector: {opt.Selector}");
            }

            if (widget.Options.Count > MaxOptionsToShow)
            {
                sb.AppendLine($"  ... and {widget.Options.Count - MaxOptionsToShow} more");
            }
        }
    }

    private static void FormatAutocomplete(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "input";
        sb.AppendLine($"Status: {widget.Options.Count} suggestions for \"{label}\"");
        sb.AppendLine($"Input value: \"{widget.CurrentValue ?? ""}\"");

        sb.AppendLine();
        if (widget.Options.Count == 0)
        {
            sb.AppendLine("No suggestions visible.");
        }
        else
        {
            sb.AppendLine("Suggestions:");
            foreach (var opt in widget.Options.Take(MaxOptionsToShow))
            {
                sb.AppendLine($"- \"{opt.Text}\" → selector: {opt.Selector}");
            }

            if (widget.Options.Count > MaxOptionsToShow)
            {
                sb.AppendLine($"  ... and {widget.Options.Count - MaxOptionsToShow} more");
            }
        }
    }

    private static void FormatDropdown(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "dropdown";
        sb.AppendLine($"Status: Dropdown opened for \"{label}\"");
        sb.AppendLine($"Current value: {widget.CurrentValue ?? "(none)"}");

        var total = widget.Metadata?.TryGetValue("totalOptions", out var t) == true ? t : null;

        sb.AppendLine();
        if (widget.Options.Count == 0)
        {
            sb.AppendLine("No options visible.");
        }
        else
        {
            var showing = total != null ? $" (showing {widget.Options.Count} of {total})" : "";
            sb.AppendLine($"Options{showing}:");
            foreach (var opt in widget.Options.Take(MaxOptionsToShow))
            {
                sb.AppendLine($"- \"{opt.Text}\" → selector: {opt.Selector}");
            }

            if (widget.Options.Count > MaxOptionsToShow)
            {
                sb.AppendLine($"  ... and {widget.Options.Count - MaxOptionsToShow} more");
            }
        }
    }

    private static void FormatSlider(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "slider";
        sb.AppendLine($"Status: Range input \"{label}\"");
        sb.AppendLine($"Current value: {widget.CurrentValue ?? "unknown"}");

        var min = widget.Metadata?.TryGetValue("min", out var mn) == true ? mn : "0";
        var max = widget.Metadata?.TryGetValue("max", out var mx) == true ? mx : "100";
        var step = widget.Metadata?.TryGetValue("step", out var st) == true ? st : "1";

        sb.AppendLine($"Range: {min} - {max} (step: {step})");
    }

    // JS interop DTOs
    private record WidgetJsResult
    {
        public string Type { get; init; } = "";
        public string? Label { get; init; }
        public string? CurrentValue { get; init; }
        public List<WidgetOptionJs>? Options { get; init; }
        public Dictionary<string, string?>? Metadata { get; init; }
    }

    private record WidgetOptionJs
    {
        public string Text { get; init; } = "";
        public string Selector { get; init; } = "";
    }

    private record NearbyActionJs
    {
        public string Text { get; init; } = "";
        public string Selector { get; init; } = "";
        public string ElementType { get; init; } = "";
    }
}
