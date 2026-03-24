using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task SendMessage_AppearsInChat()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page);

        // Type and send message
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Hello, this is an E2E test message");
        await chatInput.PressAsync("Enter");

        // User message should appear in the message list
        var userMessage = page.Locator(".message-content", new() { HasText = "Hello, this is an E2E test message" });
        await userMessage.WaitForAsync(new() { Timeout = 10_000 });

        // Wait for agent response — the assistant message element may appear early (with empty content
        // during "thinking"), so use Expect to poll until it has non-empty text content.
        var assistantMessage = page.Locator(".chat-message.assistant .message-content");
        await Assertions.Expect(assistantMessage.First)
            .Not.ToBeEmptyAsync(new() { Timeout = 120_000 });
    }

    [SkippableFact]
    public async Task SendMessage_CreatesTopicInSidebar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page);

        // Send a message to create a topic
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Create a topic for E2E testing");
        await chatInput.PressAsync("Enter");

        // A new topic should appear in the sidebar
        var topicItem = page.Locator(".topic-item");
        await topicItem.First.WaitForAsync(new() { Timeout = 30_000 });
        (await topicItem.CountAsync()).ShouldBeGreaterThan(0);
    }

    internal static async Task SelectUserAndAgentAsync(IPage page)
    {
        // Select user identity
        await page.Locator(".avatar-button").ClickAsync();
        await page.Locator(".user-dropdown-item").First.WaitForAsync(new() { Timeout = 5_000 });
        await page.Locator(".user-dropdown-item").First.ClickAsync();

        var chatInput = page.Locator("textarea.chat-input");

        // Wait up to 30s for initialization to complete and auto-select an agent.
        // The chat input becomes enabled once SignalR is connected and an agent is selected.
        try
        {
            await Assertions.Expect(chatInput).ToBeEnabledAsync(new() { Timeout = 30_000 });
            return;
        }
        catch (TimeoutException)
        {
            // Input still disabled — initialization may not have auto-selected an agent;
            // fall through to manually open the dropdown and select the first agent.
        }

        // Manually select an agent via the sidebar dropdown
        var agentDropdown = page.Locator(".dropdown-trigger");
        if (await agentDropdown.IsVisibleAsync())
        {
            await agentDropdown.ClickAsync();
            var agentItem = page.Locator(".dropdown-item").First;
            await agentItem.WaitForAsync(new() { Timeout = 10_000 });
            await agentItem.ClickAsync();
        }

        // Final wait for chat input to become enabled
        await Assertions.Expect(chatInput).ToBeEnabledAsync(new() { Timeout = 10_000 });
    }


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

    [SkippableFact]
    public async Task CancelStreaming_StopsResponse()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page);

        // Send a message that will trigger a long response
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Write a very long and detailed story about a space adventure");
        await chatInput.PressAsync("Enter");

        // Wait for Cancel button to appear (signals streaming has started)
        // The Cancel button has classes "btn btn-secondary" and text "Cancel"
        var cancelButton = page.Locator("button.btn-secondary", new() { HasText = "Cancel" });
        await cancelButton.WaitForAsync(new() { Timeout = 30_000 });

        // Click Cancel
        await cancelButton.ClickAsync();

        // Cancel button should disappear (streaming stopped)
        await Assertions.Expect(cancelButton).ToBeHiddenAsync(new() { Timeout = 10_000 });

        // Send button should reappear
        var sendButton = page.Locator("button.btn-primary", new() { HasText = "Send" });
        await Assertions.Expect(sendButton).ToBeVisibleAsync(new() { Timeout = 5_000 });
    }
}
