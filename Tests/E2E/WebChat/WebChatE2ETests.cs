using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task LoadPage_ShowsAvatarPickerAndInput()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Avatar placeholder (?) should be visible when no user selected
        var avatarPlaceholder = page.Locator(".avatar-placeholder");
        (await avatarPlaceholder.IsVisibleAsync()).ShouldBeTrue();

        // Chat input should be visible but disabled (no agent selected yet)
        var chatInput = page.Locator("textarea.chat-input");
        (await chatInput.IsVisibleAsync()).ShouldBeTrue();
        (await chatInput.IsDisabledAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task SelectUser_AvatarUpdates()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Click avatar button to open dropdown
        await page.Locator(".avatar-button").ClickAsync();

        // Dropdown should appear
        var dropdown = page.Locator(".user-dropdown-menu");
        await dropdown.WaitForAsync(new() { Timeout = 5_000 });

        // Select the first available user
        await page.Locator(".user-dropdown-item").First.ClickAsync();

        // Avatar image should replace placeholder
        var avatarImage = page.Locator("img.avatar-image");
        await avatarImage.WaitForAsync(new() { Timeout = 5_000 });
        (await avatarImage.IsVisibleAsync()).ShouldBeTrue();

        // Placeholder should no longer be visible
        var avatarPlaceholder = page.Locator(".avatar-placeholder");
        (await avatarPlaceholder.IsVisibleAsync()).ShouldBeFalse();

        // Wait for state propagation
        await page.WaitForTimeoutAsync(1_000);
    }

    [SkippableFact]
    public async Task ConnectionStatus_ShowsConnected()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // The status dot gains the "connected" class when SignalR connects.
        // Allow up to 30 seconds for the hub to become reachable and the Blazor
        // client to complete the handshake.
        var connectedDot = page.Locator(".status-dot.connected");
        await connectedDot.WaitForAsync(new() { Timeout = 30_000 });
        (await connectedDot.IsVisibleAsync()).ShouldBeTrue();
    }
}
