using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatTopicManagementE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task SelectTopic_LoadsMessages()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Send first message (creates topic 1)
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic one message for E2E");
        await chatInput.PressAsync("Enter");

        // Wait for response
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // Create new topic
        await page.Locator(".new-topic-btn").ClickAsync();

        // Send second message (creates topic 2)
        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 5_000 });
        await chatInput.FillAsync("Topic two message for E2E");
        await chatInput.PressAsync("Enter");

        // Wait for response in topic 2
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // Find topic 1 in the sidebar by its content and click it.
        // Can't rely on position — other tests' topics may also be visible.
        var topic1 = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Topic one" });
        await topic1.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await topic1.ClickAsync();

        // Verify topic 1's messages are shown
        var messageContent = page.Locator(".message-content", new PageLocatorOptions { HasText = "Topic one message for E2E" });
        await messageContent.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task DeleteTopic_RemovesFromSidebar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Send a message to create a topic
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic to delete in E2E test");
        await chatInput.PressAsync("Enter");

        // Wait for our topic to appear in sidebar (match by text content)
        var ourTopic = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Topic to delete" });
        await ourTopic.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // Click delete button on our topic (shows confirm)
        await ourTopic.Locator(".delete-btn").ClickAsync();

        // Confirm deletion
        await page.Locator(".confirm-delete-btn").ClickAsync();

        // Our specific topic should disappear
        await Assertions.Expect(ourTopic).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
    }
}