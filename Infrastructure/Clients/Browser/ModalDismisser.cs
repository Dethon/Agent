using System.Diagnostics;
using Domain.Contracts;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class ModalDismisser
{
    private static readonly IReadOnlyList<ModalPattern> _defaultPatterns =
    [
        // Cookie Consent
        new(
            Type: ModalType.CookieConsent,
            ContainerSelector:
            "[class*='cookie'], [id*='cookie'], [class*='consent'], [id*='consent'], [class*='gdpr'], [id*='gdpr'], [class*='onetrust'], [id*='onetrust']",
            ButtonSelectors:
            [
                // Common accept buttons
                "button[class*='accept'], button[id*='accept']",
                "button[class*='agree'], button[id*='agree']",
                "button[class*='allow'], button[id*='allow']",
                "a[class*='accept'], a[id*='accept']",
                // OneTrust specific
                "#onetrust-accept-btn-handler",
                ".onetrust-close-btn-handler",
                // CookieBot specific
                "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll",
                // Generic patterns
                "[data-testid*='accept']",
                "[data-action*='accept']"
            ],
            ButtonTextPatterns:
            ["accept", "agree", "allow", "ok", "got it", "aceptar", "acepto", "entendido", "permitir"]
        ),

        // Age Gate / Age Verification
        new(
            Type: ModalType.AgeGate,
            ContainerSelector:
            "[class*='age'], [id*='age'], [class*='verify'], [id*='verify'], [class*='adult'], [id*='adult'], [class*='18'], [id*='18'], [class*='ageDisclaimer'], [class*='age-disclaimer']",
            ButtonSelectors:
            [
                "button[class*='enter'], button[id*='enter']",
                "button[class*='confirm'], button[id*='confirm']",
                "button[class*='yes'], button[id*='yes']",
                "button[class*='over18'], button[class*='Over18']",
                "a[class*='enter'], a[id*='enter']",
                "a[class*='yes'], a[id*='yes']",
                "[data-action*='enter']",
                "[data-action*='confirm']",
                "[data-label*='over18']"
            ],
            ButtonTextPatterns:
            ["yes", "enter", "confirm", "i am", "soy mayor", "si", "entrar", "over 18", "over 21", "i'm over"]
        ),

        // Newsletter / Subscription Popups
        new(
            Type: ModalType.Newsletter,
            ContainerSelector:
            "[class*='newsletter'], [id*='newsletter'], [class*='subscribe'], [id*='subscribe'], [class*='popup'], [class*='modal'], [class*='overlay']",
            ButtonSelectors:
            [
                "[class*='close'], [id*='close']",
                "[aria-label*='close'], [aria-label*='dismiss'], [aria-label*='cerrar']",
                "button[class*='dismiss']",
                ".modal-close, .popup-close, .close-button",
                "[data-dismiss='modal']",
                "button.close",
                // X button patterns
                "button:has(svg[class*='close'])",
                "[class*='icon-close']"
            ],
            ButtonTextPatterns: ["close", "no thanks", "dismiss", "not now", "maybe later", "cerrar", "no gracias"]
        ),

        // Notification Permission Prompts
        new(
            Type: ModalType.Notification,
            ContainerSelector:
            "[class*='notification'], [id*='notification'], [class*='push'], [id*='push'], [class*='alert']",
            ButtonSelectors:
            [
                "button[class*='decline']",
                "button[class*='deny']",
                "button[class*='later']",
                "button[class*='no']",
                "[data-action*='decline']"
            ],
            ButtonTextPatterns: ["no", "later", "dismiss", "not now", "deny", "block", "no gracias", "ahora no"]
        )
    ];

    // How long to keep watching for a modal before concluding there is none. This is the price a
    // no-modal page pays, and the upper bound on how late an (async-injected) modal can appear and
    // still be dismissed. Chosen empirically (see ModalDismisserTests): a wall already present is
    // dismissed on the first poll regardless of this value — it only bounds the wait for one that
    // hasn't rendered yet. Missing a late wall rarely costs content (readability/selector extraction
    // strips overlays), so the window stays short to keep the common no-modal browse fast.
    private const int ModalDetectionWindowMs = 300;

    // How often to re-check for a modal within the detection window.
    private const int ModalPollIntervalMs = 75;

    public async Task<IReadOnlyList<ModalDismissed>> DismissModalsAsync(
        IPage page,
        CancellationToken ct)
    {
        var patterns = _defaultPatterns;

        // Re-attempt dismissal until something is dismissed or the window elapses. Each pass is fast
        // (immediate visibility/overlay checks, no blocking waits), and only acts on a real visible
        // overlay — so a content page with incidental modal-ish class names does nothing and a
        // no-modal page just polls cheaply until the window. This catches modals that render late
        // (async consent walls) on ANY page, and replaces the old unconditional 200ms settle + the
        // per-selector 3000/500ms WaitForAsync timeouts that every navigation paid with no modal.
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var results = await Task.WhenAll(patterns
                .Select(pattern => TryDismissPatternSafeAsync(page, pattern, ct)));
            var dismissed = results.Where(r => r != null).Cast<ModalDismissed>().ToList();

            if (dismissed.Count > 0)
            {
                // Brief wait for the close animation, then an Escape fallback for a sibling modal.
                await Task.Delay(150, ct);
                try
                {
                    await TryEscapeKeyAsync(page, ct);
                }
                catch
                {
                    // Ignore escape key failures
                }

                return dismissed;
            }

            if (sw.ElapsedMilliseconds >= ModalDetectionWindowMs)
            {
                // Nothing dismissable appeared within the window. Skip the Escape fallback: there is
                // no visible overlay, so it would only cost latency on the common no-modal page.
                return [];
            }

            await Task.Delay(ModalPollIntervalMs, ct);
        }
    }

    private async Task<ModalDismissed?> TryDismissPatternSafeAsync(
        IPage page,
        ModalPattern pattern,
        CancellationToken ct)
    {
        try
        {
            return await TryDismissPatternAsync(page, pattern, ct);
        }
        catch
        {
            // Modal dismissal is best-effort
            return null;
        }
    }

    private async Task<ModalDismissed?> TryDismissPatternAsync(
        IPage page,
        ModalPattern pattern,
        CancellationToken ct)
    {
        // Require a matching container that is an actual visible OVERLAY, not just any visible
        // element. The container selectors use generic substrings ([class*='age'], [class*='modal'],
        // …) that match ordinary article elements on content-rich pages; gating on overlay
        // positioning (fixed/absolute/sticky, on-screen, non-transparent) is what distinguishes a
        // real modal from page content. Immediate checks only — no blocking waits.
        if (!string.IsNullOrEmpty(pattern.ContainerSelector))
        {
            var containerLocator = page.Locator(pattern.ContainerSelector);
            var count = await containerLocator.CountAsync();
            if (count == 0)
            {
                return null;
            }

            var anyOverlay = false;

            for (var i = 0; i < Math.Min(count, 10); i++)
            {
                if (await IsVisibleOverlayAsync(containerLocator.Nth(i)))
                {
                    anyOverlay = true;
                    break;
                }
            }

            if (!anyOverlay)
            {
                return null;
            }
        }

        var urlBefore = page.Url;

        // Try each button selector
        foreach (var buttonSelector in pattern.ButtonSelectors)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var button = page.Locator(buttonSelector).First;

                // Immediate visibility check instead of a blocking WaitForAsync(500): absent buttons
                // are skipped in ~ms. The old per-selector waits summed to ~14s across the ~29
                // button/text probes whenever a generic container selector false-matched.
                if (!await IsCurrentlyVisibleAsync(button))
                {
                    continue;
                }

                // Skip elements that would cause navigation (links with href to different pages)
                if (await WouldCauseNavigationAsync(button, urlBefore))
                {
                    continue;
                }

                var buttonText = await button.TextContentAsync(new LocatorTextContentOptions { Timeout = 500 });
                await button.ClickAsync(new LocatorClickOptions { Timeout = 1000 });

                // Verify we didn't navigate away - if we did, this wasn't a modal dismiss
                await Task.Delay(100, ct);
                if (page.Url != urlBefore && !IsSamePageNavigation(urlBefore, page.Url))
                {
                    // Navigation occurred - go back and continue trying other patterns
                    await page.GoBackAsync(new PageGoBackOptions { Timeout = 5000 });
                    continue;
                }

                return new ModalDismissed(pattern.Type, buttonSelector, buttonText?.Trim());
            }
            catch
            {
                // Button not found or click failed, try next selector
            }
        }

        // If button selectors didn't work, try text patterns
        if (pattern.ButtonTextPatterns != null)
        {
            foreach (var textPattern in pattern.ButtonTextPatterns)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // Try button with text
                    var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = textPattern }).First;
                    if (!await IsCurrentlyVisibleAsync(button))
                    {
                        continue;
                    }

                    if (await WouldCauseNavigationAsync(button, urlBefore))
                    {
                        continue;
                    }

                    await button.ClickAsync(new LocatorClickOptions { Timeout = 1000 });

                    await Task.Delay(100, ct);
                    if (page.Url != urlBefore && !IsSamePageNavigation(urlBefore, page.Url))
                    {
                        await page.GoBackAsync(new PageGoBackOptions { Timeout = 5000 });
                        continue;
                    }

                    return new ModalDismissed(pattern.Type, $"button:text({textPattern})", textPattern);
                }
                catch
                {
                    // Text pattern not found, try next
                }

                try
                {
                    // Try link with text - but only if it doesn't navigate
                    var link = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = textPattern }).First;
                    if (!await IsCurrentlyVisibleAsync(link))
                    {
                        continue;
                    }

                    if (await WouldCauseNavigationAsync(link, urlBefore))
                    {
                        continue;
                    }

                    await link.ClickAsync(new LocatorClickOptions { Timeout = 1000 });

                    await Task.Delay(100, ct);
                    if (page.Url != urlBefore && !IsSamePageNavigation(urlBefore, page.Url))
                    {
                        await page.GoBackAsync(new PageGoBackOptions { Timeout = 5000 });
                        continue;
                    }

                    return new ModalDismissed(pattern.Type, $"link:text({textPattern})", textPattern);
                }
                catch
                {
                    // Text pattern not found, try next
                }
            }
        }

        return null;
    }

    // Returns whether the locator resolves to a visible, on-screen overlay (fixed/absolute/sticky,
    // non-zero size, not transparent/hidden) — i.e. something that looks like a real modal rather
    // than ordinary page content that merely matched a generic container selector.
    private static async Task<bool> IsVisibleOverlayAsync(ILocator locator)
    {
        try
        {
            return await locator.EvaluateAsync<bool>(
                """
                el => {
                    const r = el.getBoundingClientRect();
                    if (r.width <= 0 || r.height <= 0) return false;
                    const s = getComputedStyle(el);
                    if (s.visibility === 'hidden' || s.display === 'none' || parseFloat(s.opacity) === 0)
                        return false;
                    return s.position === 'fixed' || s.position === 'absolute' || s.position === 'sticky';
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    // Returns whether the locator currently resolves to a visible element, without blocking.
    // CountAsync()/IsVisibleAsync() report the present DOM state immediately, unlike WaitForAsync
    // which polls up to its timeout when the element is absent.
    private static async Task<bool> IsCurrentlyVisibleAsync(ILocator locator)
    {
        try
        {
            return await locator.CountAsync() > 0 && await locator.IsVisibleAsync();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WouldCauseNavigationAsync(ILocator element, string currentUrl)
    {
        try
        {
            var tagName = await element.EvaluateAsync<string>("el => el.tagName.toLowerCase()");

            // Check for anchor tags with href that would navigate away
            if (tagName == "a")
            {
                var href = await element.GetAttributeAsync("href");
                if (!string.IsNullOrEmpty(href))
                {
                    // Allow javascript:void(0), #anchors, and empty hrefs
                    if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                        href == "#" ||
                        href.StartsWith("#"))
                    {
                        return false;
                    }

                    // If it's an absolute URL to a different page, it would navigate
                    if (Uri.TryCreate(href, UriKind.Absolute, out var absoluteUri))
                    {
                        var currentUri = new Uri(currentUrl);
                        // Different host or different path = navigation
                        if (absoluteUri.Host != currentUri.Host ||
                            absoluteUri.AbsolutePath != currentUri.AbsolutePath)
                        {
                            return true;
                        }
                    }
                    else if (!href.StartsWith("#"))
                    {
                        // Relative URL that's not an anchor - likely navigation
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            // If we can't determine, assume it's safe
            return false;
        }
    }

    private static bool IsSamePageNavigation(string urlBefore, string urlAfter)
    {
        // Consider it same-page if only the fragment/hash changed
        if (Uri.TryCreate(urlBefore, UriKind.Absolute, out var uriBefore) &&
            Uri.TryCreate(urlAfter, UriKind.Absolute, out var uriAfter))
        {
            return uriBefore.GetLeftPart(UriPartial.Query) == uriAfter.GetLeftPart(UriPartial.Query);
        }

        return false;
    }

    private async Task TryEscapeKeyAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(200, ct);
    }
}