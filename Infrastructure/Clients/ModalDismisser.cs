using Domain.Contracts;
using Microsoft.Playwright;

namespace Infrastructure.Clients;

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
            "[class*='age'], [id*='age'], [class*='verify'], [id*='verify'], [class*='adult'], [id*='adult'], [class*='18'], [id*='18']",
            ButtonSelectors:
            [
                "button[class*='enter'], button[id*='enter']",
                "button[class*='confirm'], button[id*='confirm']",
                "button[class*='yes'], button[id*='yes']",
                "a[class*='enter'], a[id*='enter']",
                "a[class*='yes'], a[id*='yes']",
                "[data-action*='enter']",
                "[data-action*='confirm']"
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

    public async Task<IReadOnlyList<ModalDismissed>> DismissModalsAsync(
        IPage page,
        ModalDismissalConfig? config,
        CancellationToken ct)
    {
        var dismissed = new List<ModalDismissed>();
        var patterns = GetEffectivePatterns(config);
        var timeout = config?.TimeoutMs ?? 3000;

        // Brief wait for modals to appear
        await Task.Delay(500, ct);

        foreach (var pattern in patterns)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var result = await TryDismissPatternAsync(page, pattern, timeout, ct);
                if (result != null)
                {
                    dismissed.Add(result);
                    // Brief wait after dismissing for any animations
                    await Task.Delay(300, ct);
                }
            }
            catch
            {
                // Modal dismissal is best-effort, continue on failure
            }
        }

        // Try Escape key as fallback for generic modals
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

    private IReadOnlyList<ModalPattern> GetEffectivePatterns(ModalDismissalConfig? config)
    {
        if (config == null || !config.Enabled)
        {
            return config?.Enabled == false ? [] : _defaultPatterns;
        }

        var patterns = new List<ModalPattern>();

        // Add default patterns, excluding disabled types
        var disabledTypes = config.DisabledTypes?.ToHashSet() ?? [];
        patterns.AddRange(_defaultPatterns.Where(p => !disabledTypes.Contains(p.Type)));

        // Add custom patterns
        if (config.CustomPatterns != null)
        {
            patterns.AddRange(config.CustomPatterns);
        }

        return patterns;
    }

    private async Task<ModalDismissed?> TryDismissPatternAsync(
        IPage page,
        ModalPattern pattern,
        int timeout,
        CancellationToken ct)
    {
        // First check if the container exists
        if (!string.IsNullOrEmpty(pattern.ContainerSelector))
        {
            try
            {
                var container = page.Locator(pattern.ContainerSelector).First;
                var isVisible = await container.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = timeout });
                if (!isVisible)
                {
                    return null;
                }
            }
            catch
            {
                // Container not found or timeout
                return null;
            }
        }

        // Try each button selector
        foreach (var buttonSelector in pattern.ButtonSelectors)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var button = page.Locator(buttonSelector).First;
                var isVisible = await button.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 });

                if (isVisible)
                {
                    var buttonText = await button.TextContentAsync(new LocatorTextContentOptions { Timeout = 500 });
                    await button.ClickAsync(new LocatorClickOptions { Timeout = 1000 });

                    return new ModalDismissed(pattern.Type, buttonSelector, buttonText?.Trim());
                }
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
                    var isVisible = await button.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 });

                    if (isVisible)
                    {
                        await button.ClickAsync(new LocatorClickOptions { Timeout = 1000 });
                        return new ModalDismissed(pattern.Type, $"button:text({textPattern})", textPattern);
                    }
                }
                catch
                {
                    // Text pattern not found, try next
                }

                try
                {
                    // Try link with text
                    var link = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = textPattern }).First;
                    var isVisible = await link.IsVisibleAsync(new LocatorIsVisibleOptions { Timeout = 500 });

                    if (isVisible)
                    {
                        await link.ClickAsync(new LocatorClickOptions { Timeout = 1000 });
                        return new ModalDismissed(pattern.Type, $"link:text({textPattern})", textPattern);
                    }
                }
                catch
                {
                    // Text pattern not found, try next
                }
            }
        }

        return null;
    }

    private async Task TryEscapeKeyAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(200, ct);
    }
}