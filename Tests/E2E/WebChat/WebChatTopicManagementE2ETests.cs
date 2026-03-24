using Microsoft.Playwright;
using Shouldly;
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
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Send first message (creates topic 1)
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic one message for E2E");
        await chatInput.PressAsync("Enter");

        // Wait for response
        await page.Locator(".message-content").First.WaitForAsync(new() { Timeout = 60_000 });

        // Create new topic
        await page.Locator(".new-topic-btn").ClickAsync();

        // Send second message (creates topic 2)
        await Assertions.Expect(chatInput).ToBeEnabledAsync(new() { Timeout = 5_000 });
        await chatInput.FillAsync("Topic two message for E2E");
        await chatInput.PressAsync("Enter");

        // Wait for response in topic 2
        await page.Locator(".message-content").First.WaitForAsync(new() { Timeout = 60_000 });

        // Click topic 1 in sidebar
        var topics = page.Locator(".topic-item");
        (await topics.CountAsync()).ShouldBeGreaterThanOrEqualTo(2);

        // Topics are ordered by LastMessageAt descending (newest first),
        // so the last item in the list is the oldest topic (topic 1)
        await topics.Last.ClickAsync();

        // Verify topic 1's messages are shown
        var messageContent = page.Locator(".message-content", new() { HasText = "Topic one message for E2E" });
        await messageContent.WaitForAsync(new() { Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task DeleteTopic_RemovesFromSidebar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new() { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Send a message to create a topic
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic to delete in E2E test");
        await chatInput.PressAsync("Enter");

        // Wait for the topic to appear in sidebar
        var topicItem = page.Locator(".topic-item");
        await topicItem.First.WaitForAsync(new() { Timeout = 30_000 });

        var initialCount = await topicItem.CountAsync();

        // Click delete button on the topic (shows confirm)
        await topicItem.First.Locator(".delete-btn").ClickAsync();

        // Confirm deletion
        await page.Locator(".confirm-delete-btn").ClickAsync();

        // Topic count should decrease
        await page.WaitForFunctionAsync(
            $"() => document.querySelectorAll('.topic-item').length < {initialCount}",
            null,
            new() { Timeout = 10_000 });
    }
}
