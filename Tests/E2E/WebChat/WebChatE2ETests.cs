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
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Type and send message
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Hello, this is an E2E test message");
        await chatInput.PressAsync("Enter");

        // User message should appear in the message list
        var userMessage = page.Locator(".message-content", new PageLocatorOptions { HasText = "Hello, this is an E2E test message" });
        await userMessage.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Wait for agent response — the assistant message element may appear early (with empty content
        // during "thinking"), so use Expect to poll until it has non-empty text content.
        var assistantMessage = page.Locator(".chat-message.assistant .message-content");
        await Assertions.Expect(assistantMessage.First)
            .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 30_000 });
    }

    [SkippableFact]
    public async Task SendMessage_CreatesTopicInSidebar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Send a message to create a topic
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Create a topic for E2E testing");
        await chatInput.PressAsync("Enter");

        // A new topic should appear in the sidebar
        var topicItem = page.Locator(".topic-item");
        await topicItem.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        (await topicItem.CountAsync()).ShouldBeGreaterThan(0);
    }

    internal static async Task SelectUserAndAgentAsync(IPage page, int userIndex = 0)
    {
        // Dismiss any approval-modal-overlay left by the StreamResumeService.
        // When a new page connects, the server may push pending approval state from
        // a previous test's session, showing the overlay and blocking all clicks.
        await DismissApprovalOverlayAsync(page);

        // Select a unique user identity per test to avoid server-side state pollution.
        await page.Locator(".avatar-button").ClickAsync();
        await page.Locator(".user-dropdown-item").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });

        // The overlay can arrive asynchronously after the initial dismiss check
        // (StreamResumeService pushes state over SignalR), so retry the click if
        // an overlay intercepts it.
        try
        {
            await page.Locator(".user-dropdown-item").Nth(userIndex).ClickAsync(
                new LocatorClickOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            await DismissApprovalOverlayAsync(page);
            await page.Locator(".avatar-button").ClickAsync();
            await page.Locator(".user-dropdown-item").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
            await page.Locator(".user-dropdown-item").Nth(userIndex).ClickAsync();
        }

        var chatInput = page.Locator("textarea.chat-input");

        // Wait up to 30s for initialization to complete and auto-select an agent.
        // The chat input becomes enabled once SignalR is connected and an agent is selected.
        try
        {
            await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 30_000 });
        }
        catch (TimeoutException)
        {
            // Input still disabled — initialization may not have auto-selected an agent;
            // fall through to manually open the dropdown and select the first agent.
            var agentDropdown = page.Locator(".dropdown-trigger");
            if (await agentDropdown.IsVisibleAsync())
            {
                await agentDropdown.ClickAsync();
                var agentItem = page.Locator(".dropdown-item").First;
                await agentItem.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
                await agentItem.ClickAsync();
            }

            await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 10_000 });
        }

        // Start a fresh topic so previous test messages don't pollute context.
        // The approval overlay is pushed by StreamResumeService via a fire-and-forget
        // SignalR chain (InitializationEffect → LoadTopicHistoryAsync → TryResumeStreamAsync),
        // so it can arrive at any point — including the window between the user-select
        // retry above and this click. Guard the click with the same dismiss-and-retry
        // pattern used for the user-dropdown click.
        var newTopicBtn = page.Locator(".new-topic-btn");
        if (await newTopicBtn.IsVisibleAsync())
        {
            try
            {
                await newTopicBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                await DismissApprovalOverlayAsync(page);
                await newTopicBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
        }
    }

    private static async Task DismissApprovalOverlayAsync(IPage page)
    {
        var overlay = page.Locator(".approval-modal-overlay");
        if (await overlay.IsVisibleAsync())
        {
            var rejectBtn = page.Locator(".btn-reject");
            if (await rejectBtn.IsVisibleAsync())
            {
                await rejectBtn.ClickAsync();
            }

            await Assertions.Expect(overlay).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 5_000 });
        }
    }

    // Drives the conversation to a fully quiesced state before the test exits.
    //
    // Pending approvals live in ApprovalService._pendingApprovals — an in-memory,
    // per-container dictionary keyed by topic that survives across pages for the whole
    // suite run. It is cleared ONLY by an explicit approve/reject (RespondToApprovalAsync)
    // or DeleteTopic; the Cancel button (ChatHub.CancelTopic) does NOT clear it. If a test
    // exits while the agent has issued a (possibly follow-up) request_approval, that entry
    // leaks and StreamResumeService re-raises the overlay on every later test's page.
    //
    // Rejecting calls RespondToApprovalAsync server-side, removing the entry. The agent may
    // then issue another tool call, so loop until no overlay reappears and nothing is
    // streaming — guaranteeing no server-side approval is left behind, deterministically,
    // instead of relying on the next test to dismiss a stale overlay.
    internal static async Task DrainPendingApprovalsAsync(IPage page)
    {
        var overlay = page.Locator(".approval-modal-overlay");
        var rejectBtn = page.Locator(".btn-reject");
        var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await overlay.IsVisibleAsync())
            {
                if (await rejectBtn.IsVisibleAsync())
                {
                    await rejectBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                }

                await Assertions.Expect(overlay)
                    .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
                continue;
            }

            // No modal. While the stream is still active the agent may still emit another
            // approval, so keep polling. Once nothing is streaming and no modal reappears
            // within a stable window, the conversation is quiesced server-side.
            if (await cancelButton.IsVisibleAsync())
            {
                await page.WaitForTimeoutAsync(1_000);
                continue;
            }

            await page.WaitForTimeoutAsync(2_000);
            if (!await overlay.IsVisibleAsync() && !await cancelButton.IsVisibleAsync())
            {
                return;
            }
        }
    }


    [SkippableFact]
    public async Task LoadPage_ShowsAvatarPickerAndInput()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Avatar placeholder (?) should be visible when no user selected
        var avatarPlaceholder = page.Locator(".avatar-placeholder");
        (await avatarPlaceholder.IsVisibleAsync()).ShouldBeTrue();

        // Chat input should be visible (it may be enabled if the agent auto-selected)
        var chatInput = page.Locator("textarea.chat-input");
        (await chatInput.IsVisibleAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task SelectUser_AvatarUpdates()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Dismiss any approval-modal-overlay left by the StreamResumeService
        var overlay = page.Locator(".approval-modal-overlay");
        if (await overlay.IsVisibleAsync())
        {
            var rejectBtn = page.Locator(".btn-reject");
            if (await rejectBtn.IsVisibleAsync())
            {
                await rejectBtn.ClickAsync();
            }

            await Assertions.Expect(overlay).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 5_000 });
        }

        // Click avatar button to open dropdown
        await page.Locator(".avatar-button").ClickAsync();

        // Dropdown should appear
        var dropdown = page.Locator(".user-dropdown-menu");
        await dropdown.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });

        // Select a unique user to avoid server-side state pollution from other tests
        var userIndex = fixture.NextUserIndex();
        await page.Locator(".user-dropdown-item").Nth(userIndex).ClickAsync();

        // Avatar image should replace placeholder
        var avatarImage = page.Locator("img.avatar-image");
        await avatarImage.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000 });
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
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The status dot gains the "connected" class when SignalR connects.
        // Allow up to 30 seconds for the hub to become reachable and the Blazor
        // client to complete the handshake.
        var connectedDot = page.Locator(".status-dot.connected");
        await connectedDot.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        (await connectedDot.IsVisibleAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task ApprovalModal_ApproveFlow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        try
        {
            // Send a message that triggers a tool call.
            var chatInput = page.Locator("textarea.chat-input");
            await chatInput.FillAsync("IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. After the tool is called say 'Done' without caring for its result. this is a test");
            await chatInput.PressAsync("Enter");

            // Wait for approval modal to appear
            var approvalModal = page.Locator(".approval-modal");
            await approvalModal.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // Verify tool name is shown
            var toolName = page.Locator(".tool-name");
            (await toolName.TextContentAsync()).ShouldNotBeNullOrEmpty();

            // Click Approve
            await page.Locator(".btn-approve").ClickAsync();

            // Modal should dismiss
            await Assertions.Expect(approvalModal).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });

            // Wait for streaming to finish — the Cancel button is only visible while streaming.
            var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });
            await Assertions.Expect(cancelButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 120_000 });

            // Agent should have responded with persisted content.
            var assistantMessage = page.Locator(".chat-message.assistant .message-content").First;
            await Assertions.Expect(assistantMessage)
                .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 10_000 });
        }
        finally
        {
            // Resolve any follow-up approval the agent issued so no server-side
            // pending approval leaks into later tests (see DrainPendingApprovalsAsync).
            await DrainPendingApprovalsAsync(page);
        }
    }

    [SkippableFact]
    public async Task ApprovalModal_DenyFlow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        try
        {
            // Send a message that triggers a tool call.
            var chatInput = page.Locator("textarea.chat-input");
            await chatInput.FillAsync("IMPORTANT: You MUST call a tool right now. Use your file search/glob tool to find all files with pattern **/*. Do NOT write any text, just call the tool.");
            await chatInput.PressAsync("Enter");

            // Wait for approval modal
            var approvalModal = page.Locator(".approval-modal");
            await approvalModal.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // Click Reject
            await page.Locator(".btn-reject").ClickAsync();

            // Modal should dismiss
            await Assertions.Expect(approvalModal).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });

            // Stream should stop — Cancel button disappears (only visible while streaming)
            var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });
            await Assertions.Expect(cancelButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30_000 });
        }
        finally
        {
            // The agent often retries the rejected tool, leaving a fresh server-side
            // pending approval. Drain it so it can't pollute later tests' pages.
            await DrainPendingApprovalsAsync(page);
        }
    }

    [SkippableFact]
    public async Task CancelStreaming_StopsResponse()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Send a message that will trigger a long response
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Write a very long and detailed story about a space adventure");
        await chatInput.PressAsync("Enter");

        // Wait for Cancel button to appear (signals streaming has started)
        // The Cancel button has classes "btn btn-secondary" and text "Cancel"
        var cancelButton = page.Locator("button.btn-secondary", new PageLocatorOptions { HasText = "Cancel" });
        await cancelButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // Click Cancel
        await cancelButton.ClickAsync();

        // Cancel button should disappear (streaming stopped)
        await Assertions.Expect(cancelButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
    }
}